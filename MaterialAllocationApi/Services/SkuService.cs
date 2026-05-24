
using Dapper;
using Microsoft.EntityFrameworkCore;

public class SkuService : ISkuService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;

    public SkuService(AllocationDbContext db, IDbConnectionFactory connectionFactory)
    {
        _db = db;
        _connectionFactory = connectionFactory;
    }

    public async Task<SkuResponse> AdjustAsync(Guid id, AdjustSkuRequest request, CancellationToken ct = default)
    {
        // FindAsync returns a tracked instance - required so EF picks up Version as the concurrency token.
        // AsNoTracking would cause EF to skip the WHERE version = @expected clause entirely
        var sku = await _db.Skus.FindAsync([id],ct)
            ?? throw new NotFoundException($"SKU {id} not found.");

        InventoryAdjustment adjustment;

        try
        {
            adjustment = sku.Adjust(request.Delta, request.Reason);
        }
        catch (InvalidOperationException ex)
        {
            // Sku.Adjust throws when the delta would drive on_hand below zero
            throw new ValidationException(ex.Message);
        }

        // Stage both changes before saving - one SaveChangesAsync = one transaction
        // The adjustment row and the updated on_hand land atomically.
        _db.InventoryAdjustments.Add(adjustment);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "SKU was modified by a concurrent request. Re-read the current state and retry.",
                "CONCURRENT_MODIFICATION");
        }
        
        return ToResponse(sku);
    }

    public async Task<SkuResponse> CreateAsync(CreateSkuRequest request, CancellationToken ct = default)
    {
        Sku sku;

        try
        {
            sku = new Sku(request.SkuCode, request.Description, request.InitialOnHand);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ValidationException(ex.Message);
        }

        _db.Skus.Add(sku);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            throw new ValidationException($"SKU code '{request.SkuCode}' already exists.");
        }

        return ToResponse(sku);

    }
    public async Task<SkuResponse> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateAsync(ct);

        const string sql = @"
            SELECT 
                id as Id, 
                sku_code as SkuCode, 
                description as Description, 
                on_hand as OnHand, 
                version as Version, 
                updated_at as UpdatedAt
            FROM skus
            WHERE id = @Id
        ";

        var sku = await connection.QueryFirstOrDefaultAsync<SkuResponse>(sql, new {Id = id});;

        return sku ?? throw new NotFoundException($"SKU {id} not found.");
    }

    public async Task<PagedResult<SkuResponse>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Min(pageSize, 100);
        page = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        using var connection = await _connectionFactory.CreateAsync(ct);

        const string countSql = "SELECT COUNT(*) FROM skus";

        const string listSql = @"
            SELECT 
                id as Id, 
                sku_code as SkuCode, 
                description as Description, 
                on_hand as OnHand, 
                version as Version, 
                updated_at as UpdatedAt
            FROM skus
            ORDER BY sku_code
            LIMIT @PageSize OFFSET @Offset
        ";

        var total = await connection.ExecuteScalarAsync<int>(countSql);

        var items = (await connection.QueryAsync<SkuResponse>(
            listSql, new {PageSize = pageSize, Offset = offset}
        )).AsList();

        return new PagedResult<SkuResponse>(items, page, pageSize, total);
    }

    private SkuResponse ToResponse(Sku sku) =>
        new(sku.Id, sku.SkuCode, sku.Description, sku.OnHand, sku.Version, sku.UpdatedAt.UtcDateTime);

}