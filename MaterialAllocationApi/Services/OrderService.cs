
using Dapper;
using Microsoft.EntityFrameworkCore;

public class OrderService : IOrderService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;

    public OrderService(AllocationDbContext db, IDbConnectionFactory connectionFactory)
    {
        _db = db;
        _connectionFactory = connectionFactory;
    }

    public async Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct = default)
    {
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

        // Phase 6: for each line with allocated_qty > 0, call Sku.ReleaseUnits(line.AllocatedQty)
        // and line.ReleaseAllocation() inside this same SaveChangesAsync to restore on_hand atomically

        await _db.SaveChangesAsync(ct);

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
        Order order;
        try
        {
            order = new Order(request.ReferenceCode, request.Priority);

        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }

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

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            throw new ValidationException($"Reference code '{request.ReferenceCode}' already exists.");
        }

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