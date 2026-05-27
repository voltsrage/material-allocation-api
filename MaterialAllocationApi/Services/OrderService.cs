
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public class OrderService : IOrderService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AllocationDbContext db, IDbConnectionFactory connectionFactory, ILogger<OrderService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct = default)
    {
        // 1. Pre-flight: load order with lines for hte status check.
        // Done outside the transaction for speed - avoids locking rows for orders that
        // cannot be cancelled. The real state is re-validated inside the transaction
        // after the FOR UPDATE lock guarantees no concurrent cancel or allocate can
        // change the row
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException($"Order {id} not found.");

        try
        {
            order.Cancel();
        }
        catch (InvalidOperationException ex)
        {
            throw new ConflictException(ex.Message, "ORDER_ALREADY_CANCELLED");
        }

        // 2. Collect SKU IDs that have allocated units to release.
        // Only lines with allocated qty > 0 require a lock and a release
        // If the order has never allocated (all lines at 0), we still cancel but skip the
        // lock acquisition entirely - no inventory change needed
        var allocatedLines = order.Lines
            .Where(l => l.AllocatedQty > 0)
            .ToList();

        if(allocatedLines.Count == 0)
        {
            // Fast path: no inventory to release. Single save, no transaction needed.
            var allLineIds = order.Lines.Select(l => l.Id).ToArray();
            if (allLineIds.Length > 0)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM reservations WHERE order_line_id = ANY(@ids)",
                    new NpgsqlParameter("ids", allLineIds));
            }

            _db.OutboxMessages.Add(new OutboxMessage("order.cancelled", Helpers.Serialize(new
            {
                orderId = order.Id,
                referenceCode = order.ReferenceCode,
                releasedLines = Array.Empty<object>()
            })));

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Order {OrderId} cancelled. AllocatedLinesReleased=0.", id);
            return await GetByIdAsync(order.Id, ct);
        }

        // 3. Sort SKU IDs ascending - same order as AllocatedAsync - to prevent deadlocks
        // when a concurrent allocation and cancel share multiple SKUs.
        var skuIds = allocatedLines
            .Select(l => l.SkuId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        // 4. Begin the explicit transaction. The FOR UPDATE lock must be held until
        // SaveChanges commits - releasing it early would allow concurrent allocations
        // to read stale on_hand values between the lock release and the commit
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // 5. Lock SKU rows in deterministic order.
            // EF's identity map means the entities locked here are the same objects
            // already in memory from the pre-flight Include - no second round-trip
            // The FOR UPDATE in the SQL is what matters; EF just executes it.
            await _db.Skus
                .FromSqlRaw(
                    "SELECT * FROM skus WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
                    new NpgsqlParameter("ids", skuIds)
                ).ToListAsync();

            // 6. Release each allocated line.
            // Read allocated_qty from the tracked entity - safe because the FOR UPDATE
            // above prevents any concurrent AllocateAsync from modifying these rows
            // after we acquired the lock
            foreach(var line in allocatedLines)
            {
                var sku = await _db.Skus.FindAsync([line.SkuId], ct)
                    ?? throw new InvalidOperationException(
                        $"SKU {line.SkuId} referenced by order line {line.Id} not found."
                    );
                
                var releasedQty = line.AllocatedQty;
                sku.ReleaseUnits(line.AllocatedQty);
                line.ReleasedAllocation();

                _db.AllocationEvents.Add(new AllocationEvent(
                        AllocationEventType.AllocationReleased, id, line.Id, line.SkuId, releasedQty
                    ));
            }

            var allLineIds = order.Lines.Select(l => l.Id).ToArray();
            if(allLineIds.Length > 0)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM reservations WHERE order_line_id = ANY(@ids)",
                    new NpgsqlParameter("ids", allLineIds)
                );
            }

            _db.OutboxMessages.Add(new OutboxMessage("order.cancelled", Helpers.Serialize(new
            {
                orderId = order.Id,
                referenceCode = order.ReferenceCode,
                releasedLines = allocatedLines.Select(l => new
                {
                    skuId = l.SkuId,
                    releasedQty = l.AllocatedQty
                })
            })));
            
            // 7. Order status is already set to Cancelled by the pre-flight Cancel() call;
            // SaveChangesAsync persists:
            //  - orders.status = 'cancelled' (via EF value converter)
            //  - order_lines.allocated_qty = 0 for each released line
            //  - skus.on_hand incremented by the released amount
            // All or nothing
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation(
                "Order {OrderId} cancelled. AllocatedLinesReleased={ReleasedCount}.",
                id, allocatedLines.Count);
        }
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }

        return await GetByIdAsync(order.Id, ct);

    }

    public async Task<OrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken ct = default)
    {
        // 1. Reject duplicate SKU IDs in the same request before touching the database
        var duplicateSkuIds = request.Lines
            .GroupBy(l => l.SkuId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if(duplicateSkuIds.Any())
            throw new ValidationException(
                $"Duplicate SKU IDs in order lines: {string.Join(", ", duplicateSkuIds)}."
            );

        // 2. Verify all referenced SKUs exist in one query
        var requestedSkuIds = request.Lines.Select(l => l.SkuId)
            .Distinct().ToList();

        var existingSkuIds = await _db.Skus
            .Where(s => requestedSkuIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        var missingSkuIds = requestedSkuIds.Except(existingSkuIds).ToList();

        if(missingSkuIds.Any())
            throw new ValidationException(
                $"Unknown SKU IDs: {string.Join(", ", missingSkuIds)}"
            );

        // 3. Build the aggregate
        var order = new Order(request.ReferenceCode, request.Priority);

        foreach(var line in request.Lines)
        {
            try
            {
                order.AddLine(line.SkuId, line.RequestedQty);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new ValidationException(ex.Message);
            }
        }

        _db.Orders.Add(order);

        _db.OutboxMessages.Add(new OutboxMessage("order.created", Helpers.Serialize(new
        {
            orderId = order.Id,
            referenceCode = order.ReferenceCode,
            priority = order.Priority.ToDbString(),
            lineCount = order.Lines.Count
        })));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            throw new ValidationException($"Reference code '{request.ReferenceCode}' already exists.");
        }

        _logger.LogInformation(
            "Order {OrderId} created: referenceCode={ReferenceCode}, lines={LineCount}.",
            order.Id, order.ReferenceCode, order.Lines.Count);

        // 4. Return the full response via Dapper so SkuCode is populated in order lines.
        return await GetByIdAsync(order.Id,ct);
    }

    public async Task<OrderResponse> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateAsync(ct);

        const string sql = @"
            SELECT
                id AS Id,
                reference_code AS ReferenceCode,
                priority AS Priority,
                status AS Status,
                created_at AS CreatedAt
            FROM orders
            WHERE id = @Id;

            SELECT
                ol.id as Id,
                ol.sku_id as SkuId,
                s.sku_code as SkuCode,
                ol.requested_qty as RequestedQty,
                ol.allocated_qty as AllocatedQty
            FROM order_lines ol
            JOIN skus s ON s.id = ol.sku_id
            WHERE ol.order_id = @Id
            ORDER BY s.sku_code;
        ";

        using var multi = await connection.QueryMultipleAsync(sql, new {Id = id});

        var order = await multi.ReadFirstOrDefaultAsync<(Guid Id, string ReferenceCode, string Priority, string Status, DateTimeOffset CreatedAt)>();

        if(order == default)
            throw new NotFoundException($"Order {id} not found.");

        var lines = (await multi.ReadAsync<OrderLineResponse>()).AsList();

        return new OrderResponse(order.Id, order.ReferenceCode, order.Priority, order.Status, order.CreatedAt.UtcDateTime, lines);
    }

    public async Task<PagedResult<OrderSummaryResponse>> ListAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Min(pageSize, 100);
        page = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var connection = await _connectionFactory.CreateAsync(ct);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset",offset);

        if(status is not null)
        {
            conditions.Add("o.status = @Status");
            parameters.Add("Status", status);
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

        var countSql = $"SELECT COUNT(*) FROM orders o {where}";

        var listSql = @$"
            SELECT
                o.id as Id,
                o.reference_code as ReferenceCode,
                o.priority as Priority,
                o.status AS Status,
                COUNT(ol.id) as LineCount,
                o.created_at as CreatedAt
            FROM orders o
            LEFT JOIN order_lines ol on ol.order_id = o.id
            {where}
            GROUP BY o.id, o.reference_code, o.priority, o.status, o.created_at
            ORDER BY o.created_at DESC, o.id
            LIMIT @PageSize OFFSET @Offset
        ";

        var total = await connection.ExecuteScalarAsync<int>(countSql, parameters);

        var items = (await connection.QueryAsync<OrderSummaryResponse>(listSql, parameters)).AsList();

        return new PagedResult<OrderSummaryResponse>(items, page, pageSize, total);
    }
}