
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
        _ = await _db.Lots.FindAsync([id], ct)
            ?? throw new NotFoundException($"Lot {id} not found.");

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
        _ = await _db.Lots.FindAsync([id], ct)
            ?? throw new NotFoundException($"Lot {id} not found.");

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
        _ = await _db.Lots.FindAsync([id], ct)
            ?? throw new NotFoundException($"Lot {id} not found.");

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