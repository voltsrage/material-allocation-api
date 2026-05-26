
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

public class AllocationService : IAllocationService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<AllocationService> _logger;

    public AllocationService(AllocationDbContext db, IDbConnectionFactory connectionFactory, ILogger<AllocationService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<AllocationResponse> AllocateAsync(Guid orderId, CancellationToken ct = default)
    {
        // 1. Load the order and lines outside the transaction
        // Status check here is a fast pre-flight - the real guard is the locked state
        // inside the transaction, but we avoid acquiring locks for obviously invalid orders.
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException($"Order {orderId} not found.");

        if(order.Status == OrderStatus.Cancelled)
            throw new ConflictException(
                "Cannot allocate a cancelled order", "ORDER_CANCELLED");

        if(order.Status == OrderStatus.FullyAllocated)
            throw new ConflictException(
                "Order is already fully allocated.",
                "ORDER_FULLY_ALLOCATED"
            );

        // 2/ Determine SKU IDs to lock. Sort ascending to guarantee all transactions
        // acquire locks in the same order - prevents circular waits between concurrent
        // allocations that share multiple SKUs
        var skuIds = order.Lines
            .Select(l => l.SkuId)
            .OrderBy(id => id)
            .ToArray();

        // 3. Begin explicit transaction. The FOR UPDATE lock must be held until the
        // SaveChanges commit - an implicit EF transaction would release too early.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // 4. Lock SKU rows in deterministic order.
            // SELECT ... FOR UPDATE prevents any other transaction from reading or modifying
            // these rows until this transaction commits or rolls back.
            // ORDER BY id ensures the lock acquisition order matches our sorted skuIds

            var skus = await _db.Skus
                .FromSqlRaw(
                    "SELECT * FROM skus where Id = ANY(@ids) ORDER BY id FOR UPDATE",
                    new NpgsqlParameter("ids", skuIds)
                ).ToListAsync();

            var skuMap = skus.ToDictionary(s => s.Id);

            // Read reservations held by other orders for these SKUs.
            // Must run after the FOR UPDATE lock is acquired
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            var dbTx = (NpgsqlTransaction)tx.GetDbTransaction();

            var reservedRows = await conn.QueryAsync<(Guid SkuId, int Reserved)>
            (
                @"
                SELECT ol.sku_id as SkuId, COALESCE(SUM(r.quantity), 0) AS Reserved
                FROM reservations r
                JOIN order_lines ol ON ol.id = r.order_line_id
                WHERE ol.sku_id = ANY(@ids)
                    AND ol.order_id != @orderId
                    AND r.expires_at > NOW()
                GROUP BY ol.sku_id
                ",
                new {ids = skuIds, orderId},
                transaction: dbTx
            );

            var reservedByOthers = reservedRows.ToDictionary(r => r.SkuId, r => r.Reserved);

            // 5. Allocate each line in sku_id order (same as lock order - belt and suspenders)
            var results = new List<AllocationLineResult>();

            foreach(var line in order.Lines.OrderBy(l => l.SkuId))
            {
                if(!skuMap.TryGetValue(line.SkuId, out var sku))
                    throw new ValidationException(
                        $"SKU {line.SkuId} on order line was not found during allocation"
                    );

                var remaining = line.RequestedQty - line.AllocatedQty;

                var othersHeld = reservedByOthers.GetValueOrDefault(sku.Id);
                var available = Math.Max(0, sku.OnHand - othersHeld);
                var canAllocate = Math.Min(available, remaining);

                if(canAllocate > 0)
                {
                    // AllocateUnits decrements on_hand; Allocate increments allocated_qty.
                    // Both entities are tracked - SaveChangesAsync persists both atomically
                    sku.AllocateUnits(canAllocate);
                    line.Allocate(canAllocate);
                }

                results.Add(new AllocationLineResult(
                    line.SkuId,
                    sku.SkuCode,
                    line.RequestedQty,
                    line.AllocatedQty,
                    line.RequestedQty - line.AllocatedQty
                ));
            }

            // 6. Recompute order status from updated line quantities
            // RecomputeStatus reads Lines - they are loaded (Include above) and updated in-memory
            order.RecomputeStatus();

            // 7. One SaveChangesAsync = one implicit transaction batch. Commits
            // - skus.on_hand decrements (one UPDATE per SKU)
            // - order_lines.allocated_qty increments (one UPDATE PER Line)
            // - order.status update
            // All or nothing. If any write fails, the catch block rolls back.

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} allocated: status={Status}, lines={LineCount}, fullyAllocated={IsFullyAllocated}",
                orderId, order.Status.ToDbString(), results.Count, order.Status == OrderStatus.FullyAllocated);

            return new AllocationResponse(
                order.Id,
                order.Status.ToDbString(),
                order.Status == OrderStatus.FullyAllocated,
                results
            );
        }       
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }    

    public async Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateAsync(ct);

        const string sql = @"
            SELECT 
                id as Id, 
                sku_code as SkuCode, 
                on_hand as OnHand
            FROM skus
            WHERE id = @SkuId
        ";

        var row = await connection.QueryFirstOrDefaultAsync<SkuAvailRow>(sql, new {SkuId = skuId});

        if(row is null)
            throw new NotFoundException($"SKU {skuId} not found.");

        // Formula: available = on_hand - reserved - allocated
        // on_hand is mutable: AllocateUnits() decrements it at commit time, so on_hand already
        // excludes committed allocations. Reserved = 0 until Phase 7 adds the reservations table.
        // Phase 7 will query: SELECT COALESCE(SUM(quantity), 0) FROM reservations
        // WHERE order_line_id in (...) AND expires_at > NOW()
        // and subtract that from on_hand here.
        var reserved = await connection.ExecuteScalarAsync<int>(
            @"
            SELECT COALESCE(SUM(r.quantity), 0)
            FROM reservations r
            JOIN order_lines ol ON ol.id = r.order_line_id
            WHERE ol.sku_id = @SkuId
                AND r.expires_at > NOW()
            ",
            new {SkuId = skuId}
        );
        return new AvailabilityResponse(row.Id, row.SkuCode, row.OnHand, reserved, Math.Max(0, row.OnHand - reserved));
    }

    private record SkuAvailRow(Guid Id, string SkuCode, int OnHand);
}