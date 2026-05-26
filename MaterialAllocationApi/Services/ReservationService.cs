
using Dapper;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

public class ReservationService : IReservationService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ReservationService> _logger;

    public ReservationService(AllocationDbContext db, IDbConnectionFactory connectionFactory, ILogger<ReservationService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        var deleted = await conn.ExecuteAsync(
            "DELETE FROM reservations WHERE expires_at <= @now",
            new {now = DateTimeOffset.UtcNow}
        );

        if(deleted > 0)
            _logger.LogInformation("Expired {Count} reservation(s).", deleted);

        return deleted;
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct = default)
    {
        var deleted = await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM reservations WHERE id = @id;",
            new NpgsqlParameter("id", reservationId)
        );

        if(deleted == 0)
            throw new NotFoundException($"Reservation {reservationId} not found.");
    }

    public async Task<ReservationResponse> ReserveAsync(Guid orderId, ReserveRequest request, CancellationToken ct = default)
    {
        // 1. Pre-flight: load order with lines
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException($"Order {orderId} not found!");

        if(order.Status == OrderStatus.Cancelled)
            throw new ConflictException(
                "Cannot reserve for a cancelled order.",
                "ORDER_CANCELLED"
            );

        if(order.Status == OrderStatus.FullyAllocated)
            throw new ConflictException(
                "Order is fully allocated; all lines are satisfied, nothing to reserve.",
                "ORDER_FULLY_ALLOCATED"
            );

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(request.TtlMinutes);

        // 2. Sort SKU IDs for deterministic lock order - same convention as AllocateAsync
        var skuIds = order.Lines
            .Select(l => l.SkuId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        await using var tx  = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // 3. Lock SKY rows in sorted order. All quantity-changing operations (allocate,
            // reserve) acquire these locks first so the read of reservations below
            // is guaranteed to see the latest committed stats.
            var skus = await _db.Skus
                .FromSqlRaw(
                    "SELECT * FROM skus WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
                    new NpgsqlParameter("ids", skuIds)
                ).ToListAsync(ct);

            var skuMap = skus.ToDictionary(s => s.Id);

            // 4. Get the shared connection (already enrolled in the transaction)
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            var dbTx = (NpgsqlTransaction)tx.GetDbTransaction();

            // 5. Read reservations held by OTHER orders for these SKUs in one query.
            // Safe because we hold FOR UPDATE on the SKU rows - no concurrent reserve
            // or allocate can insert new reservations for these SKUs until we commit
            var reservedRows = await conn.QueryAsync<(Guid SkuId, int Reserved)>(
                """
                SELECT ol.sku_id as SkuId, COALESCE(SUM(r.quantity), 0) AS Reserved
                FROM reservations r
                JOIN order_lines ol ON ol.id = r.order_line_id
                WHERE ol.sku_id = ANY(@skuIds)
                    AND ol.order_id != @orderId
                    AND r.expires_at > NOW()
                GROUP BY ol.sku_id
                """,
                new {skuIds, orderId},
                transaction: dbTx
            );

            var reservedByOthers = reservedRows.ToDictionary(r => r.SkuId, r => r.Reserved);

            // 6.Delete existing active reservations for this order's lines
            // Calling reserve again refreshes the TTL - idempotent behavior
            var lineIds = order.Lines.Select(l => l.Id).ToArray();
            await conn.ExecuteAsync(
                "DELETE FROM reservations WHERE order_line_id = ANY(@lineIds)",
                new { lineIds },
                transaction: dbTx
            );

            // 7. Create new reservations for lines with remaining demand and available stock.
            var results = new List<ReservationLineResult>();

            foreach(var line in order.Lines.OrderBy(l => l.SkuId))
            {
                var remaining = line.RequestedQty - line.AllocatedQty;
                if(remaining <= 0) continue; // line already fully allocated

                if(!skuMap.TryGetValue(line.SkuId, out var sku)) continue;

                var othersHeld = reservedByOthers.GetValueOrDefault(line.SkuId);
                var available = Math.Max(0, sku.OnHand - othersHeld);
                var canReserve = Math.MinMagnitude(available, remaining);

                if(canReserve <= 0) continue; // no stock to reserve for this line

                var skuCode = sku.SkuCode;
                _db.Reservations.Add(new Reservation(line.Id, (int)canReserve, expiresAt));

                results.Add(new ReservationLineResult(
                    line.Id, line.SkuId, skuCode, (int)canReserve, expiresAt.UtcDateTime));
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new ReservationResponse(order.Id, order.ReferenceCode, results, expiresAt.UtcDateTime);
        }
        catch (System.Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }
}