
using System.Diagnostics.Contracts;
using Dapper;
using Microsoft.EntityFrameworkCore;

public class ContractService : IContractService
{
    private readonly AllocationDbContext _db;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ContractService> _logger;

    public ContractService(
        AllocationDbContext db, 
        IDbConnectionFactory connectionFactory, 
        ILogger<ContractService> logger)
    {
        _db = db;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<ContractResponse> CreateAsync(Guid customerId, CreateContractRequest request, CancellationToken ct = default)
    {
        var customerExists = await _db.Customers
            .AnyAsync(c => c.Id == customerId, ct);
        if(!customerExists)
            throw new NotFoundException($"Customer {customerId} not found.");

        var skuExists = await _db.Skus
            .AnyAsync(s => s.Id == request.SkuId);
        if(!skuExists)
            throw new NotFoundException($"Sku {request.SkuId} not found.");

        // Reject if an active contract for this (customer, SKU) already overlaps the requested period.
        // An open-ended existing contract (effective_to is NULL) always overlaps
        var overlaps = await _db.CustomerContracts.AnyAsync(c =>
            c.CustomerId == customerId &&
            c.SkuId == request.SkuId &&
            c.EffectiveFrom <= (request.EffectiveTo ?? DateOnly.MaxValue) &&
            (c.EffectiveTo == null || c.EffectiveTo >= request.EffectiveFrom), ct 
        );

        if(overlaps)
            throw new ValidationException(
                "A contract for this customer and SKU already covers part of the requested period"
            );

        var contract = new CustomerContract(
            customerId, request.SkuId,
            request.FloorQty, request.CeilingQty,
            request.EffectiveFrom, request.EffectiveTo
        );

        _db.CustomerContracts.Add(contract);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
        "Contract {ContractId} created: customer={CustomerId}, sku={SkuId}, floor={Floor}, ceiling={Ceiling}.",
        contract.Id, customerId, request.SkuId, request.FloorQty, request.CeilingQty?.ToString() ?? "uncapped");

        return await QueryContractByIdAsync(contract.Id, ct);

    }

    private async Task<ContractResponse> QueryContractByIdAsync(Guid contractId, CancellationToken ct)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        return await conn.QueryFirstAsync<ContractResponse>(
            @"
            SELECT
                cc.Id AS Id,
                cc.customer_id AS CustomerId,
                cc.sku_id AS SkuId,
                s.sku_code AS SkuCode,
                cc.floor_qty AS FloorQty,
                cc.ceiling_qty AS CeilingQty,
                cc.effective_from AS EffectiveFrom,
                cc.effective_to AS EffectiveTo,
                cc.created_at AS CreatedAt
            FROM customer_contracts cc
            JOIN skus s on s.id = cc.sku_id
            WHERE cc.id = @contractId
            ",
            new {contractId}
        );
    }

    public async Task<IReadOnlyList<ContractUtilizationResponse>> GetUtilizationAsync(Guid customerId, CancellationToken ct = default)
    {
        _ = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
            ?? throw new NotFoundException($"Customer {customerId} not found.");

        using var conn = await _connectionFactory.CreateAsync(ct);

        // allocated_qty is the live total on non-cancelled order lines for this customer and sku
        // It reflects all allocation runs to date, not just the most recent one
        var rows = await conn.QueryAsync<ContractUtilizationResponse>(
            @"
            SELECT
                cc.id as ContractId,
                cc.sku_id as SkuId,
                s.sku_code AS SkuCode,
                cc.floor_qty as FloorQty,
                cc.ceiling_qty as CeilingQty,
                cc.effective_from as EffectiveFrom,
                cc.effective_to as EffectiveTo,
                COALESCE(SUM(ol.allocated_qty)::int, 0) as AllocatedQty
            FROM customer_contracts cc
            JOIN skus s on s.id = cc.sku_id
            LEFT JOIN orders o
                ON o.customer_id = cc.customer_id
            LEFT JOIN order_lines ol ON ol.order_id = o.id AND ol.sku_id = cc.sku_id
            WHERE cc.customer_id = @customerId
                AND cc.effective_from <= CURRENT_DATE
                AND (cc.effective_to IS NULL OR cc.effective_to >= CURRENT_DATE)
            GROUP BY cc.id, s.sku_code
            ORDER BY s.sku_code
            ",
            new {customerId}
        );

        return rows.AsList();
    }

    public async Task<IReadOnlyList<ContractResponse>> ListAsync(Guid customerId, CancellationToken ct = default)
    {
        _ = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
            ?? throw new NotFoundException($"Customer {customerId} not found.");

        using var conn = await _connectionFactory.CreateAsync(ct);

        var rows = await conn.QueryAsync<ContractResponse>(
            @"
            SELECT
                cc.id as Id,
                cc.customer_id as CustomerId,
                cc.sku_id as SkuId,
                s.sku_code as SkuCode,
                cc.floor_qty as FloorQty,
                cc.ceiling_qty as CeilingQty,
                cc.effective_from as EffectiveFrom,
                cc.effective_to AS EffectiveTo,
                cc.created_at as CreatedAt
            FROM customer_contracts cc
            JOIN skus s ON s.id = cc.sku_id
            WHERE cc.customer_id = @customerId
            ORDER BY cc.effective_from DESC, s.sku_code
            ",
            new {customerId}
        );

        return rows.ToList();
    }
}