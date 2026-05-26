
using Dapper;

public class RollupService : IRollupService
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RollupService(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<PagedResult<SkuShortageResponse>> GetSkuShortageAsync(int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Min(pageSize, 100);
        page     = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var conn = await _connectionFactory.CreateAsync(ct);

        // The CTE is defined once and referenced by both the COUNT and the paged SELECT
        // Defining it inline avoids duplicating the JOIN logic between two separate queries
        const string cte = @"
            WITH shortage_data AS (
                SELECT
                    s.id as Id,
                    s.sku_code as SkuCode,
                    s.description as Description,
                    s.on_hand as OnHand,
                    COALESCE(res.active_reserved, 0)::INT as Reserved,
                    (s.on_hand - COALESCE(res.active_reserved, 0))::INT as Available,
                    COALESCE(demand.open_demand, 0)::INT as OpenDemand,
                    (COALESCE(demand.open_demand, 0) -
                        (s.on_hand - COALESCE(res.active_reserved, 0)))::INT as Shortage
                FROM skus s
                LEFT JOIN (
                    -- Active reservations per SKU: sum only non-expired row.
                    SELECT ol.sku_id, SUM(r.quantity) as active_reserved
                    FROM reservations r
                    JOIN order_lines ol ON ol.id = r.order_line_id
                    WHERE r.expires_at > NOW()
                    GROUP BY ol.sku_id
                ) res ON res.sku_id = s.id
                LEFT JOIN (
                    -- Open demand per SKU: unfulfilled quantity not covered by an active reservation
                    SELECT ol.sku_id,
                        SUM(GREATEST(ol.requested_qty - ol.allocated_qty - COALESCE(line_res.line_reserved, 0)::INT, 0)) AS open_demand
                    FROM order_lines ol
                    JOIN orders o ON o.id = ol.order_id
                    LEFT JOIN (
                        SELECT order_line_id, SUM(quantity) AS line_reserved
                        FROM reservations
                        WHERE expires_at > NOW()
                        GROUP BY order_line_id
                    ) line_res ON line_res.order_line_id = ol.id
                    WHERE o.status IN ('open', 'partially_allocated')
                        AND ol.requested_qty > ol.allocated_qty
                    GROUP BY ol.sku_id
                ) demand on demand.sku_id = s.id
                -- Only emit rows that are actually short
                WHERE COALESCE(demand.open_demand, 0)
                    > (s.on_hand - COALESCE(res.active_reserved, 0))
            )
        ";

        var total = await conn.ExecuteScalarAsync<int>(
            $"{cte} SELECT COUNT(*) FROM shortage_data"
        );

        var items = (await conn.QueryAsync<SkuShortageResponse>(
            @$"
            {cte}
            SELECT *
            FROM shortage_data
            ORDER BY Shortage DESC, SkuCode
            LIMIT @pageSize OFFSET @offset
            ",
            new {pageSize, offset}
        )).AsList();

        return new PagedResult<SkuShortageResponse>(items, page, pageSize, total);
    }
}