
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public class LotService : ILotService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private ILogger<LotService> _logger;

    public LotService(
        AllocationDbContext db,
        IDbConnectionFactory connectionFactory,
        ILogger<LotService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<PagedResult<LotAllocationHistoryEntry>> GetAllocationsAsync(Guid lotId, int page, int pageSize, CancellationToken ct = default)
    {
        _ = await _db.Lots.FindAsync([lotId], ct)
        ?? throw new NotFoundException($"Lot {lotId} not found.");

        pageSize = Math.Min(pageSize, 100);
        page     = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var conn = await _connectionFactory.CreateAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            @"
            SELECT COUNT(*)
            FROM allocation_events
            WHERE lot_id = @lotId AND event_type = 'allocation_committed'
            ",
            new {lotId}
        );

        var items = (await conn.QueryAsync<LotAllocationHistoryEntry>(
            @"
            SELECT
                o.id as OrderId,
                o.reference_code as ReferenceCode,
                o.priority AS Priority,
                o.status AS OrderStatus,
                ae.order_line_id AS OrderLineId,
                ae.quantity as QuantityConsumed,
                ae.occurred_at AS OccurredAt
            FROM allocation_events ae
            JOIN orders o on o.id = ae.order_id
            WHERE ae.lot_id = @lotId
                AND ae.event_type = 'allocation_committed'
            ORDER BY ae.occurred_at DESC
            LIMIT @pageSize OFFSET @offset
            ",
            new {lotId, pageSize, offset}
        )).AsList();

        return new PagedResult<LotAllocationHistoryEntry>(items, page, pageSize, total);
    }

    public async Task<LotResponse> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<LotResponse>(
            @"
            SELECT
                l.id AS Id,
                l.sku_id AS SkuId,
                s.sku_code as SkuCode,
                l.lot_code as LotCode,
                l.quantity as Quantity,
                l.available_qty AS AvailableQty,
                l.status AS Status,
                l.received_at as ReceivedAt,
                l.created_at AS CreatedAt
            FROM lots l
            JOIN skus s on s.id = l.sku_id
            WHERE l.id = @id
            ",
            new {id}
        );

        return row ?? throw new NotFoundException($"Lot {id} not found.");
    }

    public async Task<IReadOnlyList<LotEventHistoryEntry>> GetEventsAsync(Guid lotId, CancellationToken ct = default)
    {
        _ = await _db.Lots.FindAsync([lotId], ct)
            ?? throw new NotFoundException($"Lot {lotId} not found.");

        using var conn = await _connectionFactory.CreateAsync(ct);

        var rows = await conn.QueryAsync<LotEventHistoryEntry>(
            @"
            SELECT
                event_type as EventType,
                quantity_affected as QuantityAffected,
                notes as Notes,
                occurred_at AS OccurredAt
            FROM lot_events
            WHERE lot_id = @lotId
            ORDER BY occurred_at
            ",
            new {lotId}
        );

        return rows.AsList();
    }

    public async Task<IReadOnlyList<OrderLotProvenanceEntry>> GetOrderLotsAsync(Guid orderId, CancellationToken ct)
    {
        _ = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException($"Order {orderId} not found.");
        
        using var conn = await _connectionFactory.CreateAsync(ct);

        var rows = await conn.QueryAsync<OrderLotProvenanceEntry>(
            @"
            SELECT
                l.id AS LotId,
                l.lot_code AS LotCode,
                l.sku_id as SkuId,
                s.sku_code as SkuCode,
                SUM(ae.quantity)::INT AS QuantityConsumed,
                l.status as LotStatus,
                l.received_at as ReceivedAt
            FROM allocation_events ae
            JOIN lots l ON l.id = ae.lot_id
            JOIN skus s on s.id = l.sku_id
            WHERE ae.order_id = @orderId
                AND ae.event_type = 'allocation_committed'
                AND ae.lot_id IS NOT NULL
            GROUP BY l.id, s.sku_code
            ORDER BY s.sku_code, l.received_at
            ",
            new {orderId}
        );

        return rows.AsList();
    }

    public async Task<SkuLotSnapshotResponse> GetSkuSnapshotAsync(Guid skuId, CancellationToken ct)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        // Three queries in on round trip via QueryMultipleAsync
        using var multi = await conn.QueryMultipleAsync(
            @"
            SELECT 
                id as SkuId,
                sku_code as SkuCode,
                on_hand AS OnHand
            FROM skus
            WHERE id = @skuId;

            SELECT
                status as Status,
                COUNT(*)::INT as LotCount,
                COALESCE(SUM(available_qty), 0)::INT as TotalAvailableQty
            FROM lots
            WHERE sku_id = @skUId
            GROUP by status
            ORDER BY status;

            SELECT
                id as LotId,
                lot_code as LotCode,
                quantity as Quantity,
                available_qty AS AvailableQty,
                status as Status,
                received_at as Received_at
            FROM lots
            WHERE sku_id = @skuId
            ORDER BY received_at DESC, lot_code
            ",
            new {skuId}
        );

        var skuRow = await multi.ReadFirstOrDefaultAsync<(Guid SkuId, string SkuCode, int OnHand)>();

        if(skuRow == default)
            throw new NotFoundException($"SKU {skuId} not found.");

        var summary = (await multi.ReadAsync<LotStatusSummary>()).AsList();
        var lots = (await multi.ReadAsync<LotSnapshotEntry>()).AsList();

        return new SkuLotSnapshotResponse(
            skuRow.SkuId, skuRow.SkuCode, skuRow.OnHand,
            summary.AsReadOnly(),
            lots.AsReadOnly()
        );
    }

    public async Task<PagedResult<LotResponse>> ListBySkuAsync(Guid skuId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        _ = await _db.Skus.FindAsync([skuId], ct) 
            ?? throw new NotFoundException($"SKU {skuId} not found.");

        pageSize = Math.Min(pageSize, 100);
        page     = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var conn = await _connectionFactory.CreateAsync(ct);

        var conditions = new List<string>{"l.sku_id = @skuId"};
        var parameters = new DynamicParameters();
        parameters.Add("skuId", skuId);
        parameters.Add("pageSize", pageSize);
        parameters.Add("offset", offset);

        if (!string.IsNullOrEmpty(status))
        {
            conditions.Add("l.status = @status");
            parameters.Add("status", status);
        }

        var where = string.Join(" AND ", conditions);

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM lots l WHERE {where}", parameters
        );

        var items = (await conn.QueryAsync<LotResponse>(
            @$"
            SELECT
                l.id AS Id,
                l.sku_id AS SkuId,
                s.sku_code AS SkuCode,
                l.lot_code AS LotCode,
                l.quantity AS Quantity,
                l.available_qty AS AvailableQty,
                l.status AS Status,
                l.received_at AS ReceivedAt,
                l.created_at AS CreatedAt
            FROM lots l
            JOIN skus s on s.id = l.sku_id
            WHERE {where}
            ORDER BY l.received_at DESC, l.lot_code
            LIMIT @pageSize OFFSET @offset
            ",
            parameters
        )).AsList();

        return new PagedResult<LotResponse>(items, page, pageSize, total);
    }

    public async Task<LotResponse> QuarantineAsync(Guid id, string? notes, CancellationToken ct = default)
    {
        // Pre-flight: verify existence outside the transaction to avoid locking for missing lots.
        // AnyAsync (not FindAsync) so the entity is NOT loaded into the identity map — if FindAsync
        // were used, EF Core would return the stale tracked entity inside the transaction instead of
        // the fresh DB row locked by FOR UPDATE, causing a concurrent quarantine to see stale
        // status=available and drive on_hand negative via HoldForQuarantine.
        if (!await _db.Lots.AnyAsync(l => l.Id == id, ct))
            throw new NotFoundException($"Lot {id} not found.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // Lock SKU row first (consistent with Phase 4 lock order)
            // FromSqlRaw updates EF's identity map so sku reflects the latest committed state
            var lotForSku = await _db.Lots
                .AsNoTracking()
                .Where(l => l.Id == id)
                .Select(l => l.SkuId)
                .FirstAsync(ct);

            var skus = await _db.Skus
                .FromSqlRaw("SELECT * FROM skus WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", lotForSku))
                    .ToListAsync(ct);

            var sku = skus.Single();

            // Lock lot row second, after SKU lock is held.
            var lots = await _db.Lots
                .FromSqlRaw("SELECT * FROM lots WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", id))
                .ToListAsync(ct);

            var lot = lots.Single();

            int delta;
            try
            {
                delta = lot.Quarantine();
            }
            catch (InvalidOperationException ex)
            {
                throw new ConflictException(ex.Message, "LOT_INVALID_STATUS_TRANSITION");
            }

            sku.HoldForQuarantine(delta);

            _db.LotEvents.Add(new LotEvent(lot.Id, lot.SkuId, LotEventType.Quarantined, delta, notes));

            _db.OutboxMessages.Add(new OutboxMessage("lot.quarantined", Helpers.Serialize(new
            {
                lotId = lot.Id,
                lotCode = lot.LotCode,
                skuId = lot.SkuId,
                quantityAffected = delta,
                notes
            })));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Lot {LotId} quarantined: skuId={SkuId}, quantityAffected={Qty}.",
                lot.Id, lot.SkuId, delta);

            return await QueryLotByIdAsync(lot.Id, ct);
        }
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }

    public async Task<LotResponse> ReceiveAsync(Guid skuId, ReceiveLotRequest request, CancellationToken ct = default)
    {
        // FindAsync returns the tracked instance so ReceiveLot mutates the row EF will UPDATE
        var sku = await _db.Skus.FindAsync([skuId], ct)
            ?? throw new NotFoundException($"SKU {skuId} not found.");

        var receivedAt = request.ReceivedAt ?? DateTimeOffset.UtcNow;
        var lot = new Lot(skuId, request.LotCode, request.Quantity, receivedAt);

        sku.ReceiveLot(request.Quantity);

        _db.Lots.Add(lot);

        _db.OutboxMessages.Add(new OutboxMessage("lot.received", Helpers.Serialize(new
        {
            lotId = lot.Id,
            lotCode = lot.LotCode,
            skuId = skuId,
            skuCode = sku.SkuCode,
            quantity = lot.Quantity
        })));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            throw new ValidationException($"Lot code '{request.LotCode}' already exists.");
        }

        _logger.LogInformation(
            "Lot {LotId} received: code={LotCode}, sku={SkuId}, quantity={Quantity}, receivedAt={ReceivedAt}.",
            lot.Id, lot.LotCode, skuId, lot.Quantity, receivedAt);

        return await QueryLotByIdAsync(lot.Id, ct);
    }

    public async Task<LotResponse> ReleaseAsync(Guid id, string? notes, CancellationToken ct = default)
    {
        // Pre-flight: verify existence outside the transaction to avoid locking for missing lots.
        if (!await _db.Lots.AnyAsync(l => l.Id == id, ct))
            throw new NotFoundException($"Lot {id} not found.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // Lock SKU row first (consistent with Phase 4 lock order)
            // FromSqlRaw updates EF's identity map so sku reflects the latest committed state
            var lotForSku = await _db.Lots
                .AsNoTracking()
                .Where(l => l.Id == id)
                .Select(l => l.SkuId)
                .FirstAsync(ct);

            var skus = await _db.Skus
                .FromSqlRaw("SELECT * FROM skus WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", lotForSku))
                    .ToListAsync(ct);

            var sku = skus.Single();

            // Lock lot row second, after SKU lock is held.
            var lots = await _db.Lots
                .FromSqlRaw("SELECT * FROM lots WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", id))
                .ToListAsync(ct);

            var lot = lots.Single();

            int delta;
            try
            {
                delta = lot.Release();
            }
            catch (InvalidOperationException ex)
            {
                throw new ConflictException(ex.Message, "LOT_INVALID_STATUS_TRANSITION");
            }

            sku.RestoreFromQuarantine(delta);

            _db.LotEvents.Add(new LotEvent(lot.Id, lot.SkuId, LotEventType.Released, delta, notes));

            _db.OutboxMessages.Add(new OutboxMessage("lot.released", Helpers.Serialize(new
            {
                lotId = lot.Id,
                lotCode = lot.LotCode,
                skuId = lot.SkuId,
                quantityAffected = delta,
                notes
            })));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Lot {LotId} quarantined: skuId={SkuId}, quantityAffected={Qty}.",
                lot.Id, lot.SkuId, delta);

            return await QueryLotByIdAsync(lot.Id, ct);
        }
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }

    public async Task<LotResponse> ScrapAsync(Guid id, string? notes, CancellationToken ct = default)
    {
        // Pre-flight: verify existence outside the transaction to avoid locking for missing lots.
        if (!await _db.Lots.AnyAsync(l => l.Id == id, ct))
            throw new NotFoundException($"Lot {id} not found.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // Lock SKU row first (consistent with Phase 4 lock order)
            // FromSqlRaw updates EF's identity map so sku reflects the latest committed state
            var lotForSku = await _db.Lots
                .AsNoTracking()
                .Where(l => l.Id == id)
                .Select(l => l.SkuId)
                .FirstAsync(ct);

            var skus = await _db.Skus
                .FromSqlRaw("SELECT * FROM skus WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", lotForSku))
                    .ToListAsync(ct);

            var sku = skus.Single();

            // Lock lot row second, after SKU lock is held.
            var lots = await _db.Lots
                .FromSqlRaw("SELECT * FROM lots WHERE id = @id FOR UPDATE",
                    new NpgsqlParameter("id", id))
                .ToListAsync(ct);

            var lot = lots.Single();

            int delta;
            try
            {
                delta = lot.Scrap();
            }
            catch (InvalidOperationException ex)
            {
                throw new ConflictException(ex.Message, "LOT_INVALID_STATUS_TRANSITION");
            }

            // delta == 0 when scrapping from Quarantined: sku.OnHand was already decremented at quarantine time.
            if(delta > 0)
                sku.HoldForQuarantine(delta);

            _db.LotEvents.Add(new LotEvent(lot.Id, lot.SkuId, LotEventType.Scrapped, delta, notes));

            _db.OutboxMessages.Add(new OutboxMessage("lot.scrapped", Helpers.Serialize(new
            {
                lotId = lot.Id,
                lotCode = lot.LotCode,
                skuId = lot.SkuId,
                quantityAffected = delta,
                notes
            })));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Lot {LotId} quarantined: skuId={SkuId}, quantityAffected={Qty}.",
                lot.Id, lot.SkuId, delta);

            return await QueryLotByIdAsync(lot.Id, ct);
        }
        catch (Exception)
        {
            await TransactionHelper.RollbackAsync(tx);
            throw;
        }
    }

    private async Task<LotResponse> QueryLotByIdAsync(Guid id, CancellationToken ct)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        return await conn.QueryFirstAsync<LotResponse>(
            @"
            SELECT
                l.id AS Id,
                l.sku_id AS SkuId,
                s.sku_code as SkuCode,
                l.lot_code as LotCode,
                l.quantity as Quantity,
                l.available_qty AS AvailableQty,
                l.status AS Status,
                l.received_at AS ReceivedAt,
                l.created_at AS CreatedAt
            FROM lots l
            JOIN skus s on s.id = l.sku_id
            WHERE l.id = @id
            ",
            new {id}
        );
    }
}