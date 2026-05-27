
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;

public class AllocationRunService : IAllocationRunService
{
    private readonly AllocationDbContext _db;
    private readonly ILogger<AllocationRunService> _logger;
    private  readonly IDbConnectionFactory _connectionFactory;

    public AllocationRunService(
        AllocationDbContext db, 
        ILogger<AllocationRunService> logger, 
        IDbConnectionFactory connectionFactory)
    {
        _db = db;
        _logger = logger;
        _connectionFactory = connectionFactory;
    }

    public async Task<EnqueueResult> EnqueueAsync(string? requestedBy, CancellationToken ct = default)
    {
        var inProgress = await _db.AllocationRuns
            .Where(r => r.Status == "pending" || r.Status == "running")
            .OrderBy(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        
        if(inProgress is not null)
            return new EnqueueResult.Conflict(inProgress.Id, inProgress.Status);

        var run = new AllocationRun(requestedBy);
        _db.AllocationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Allocation run {RunId} enqueued by {RequestedBy}.",
        run.Id, requestedBy ?? "unknown");

        return new EnqueueResult.Accepted(run);
    }

    public async Task<AllocationRunStatusResponse> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<AllocationRunRow>(
            @"
            SELECT
                id AS Id,
                status AS Status,
                requested_at AS RequestedAt,
                started_at AS StartedAt,
                completed_at AS CompletedAt,
                requested_by as RequestedBy,
                error AS Error,
                orders_fully_allocated as OrdersFullyAllocated,
                orders_partially_allocated as OrdersPartiallyAllocated,
                orders_processed as OrdersProcessed,
                results as Results
            FROM allocation_runs
            WHERE id = @id
            ",
            new {id}
        );

        if(row is null)
            throw new NotFoundException($"Allocation run {id} not found.");

        IReadOnlyList<AllocationRunResult>? results = null;

        if(row.Results is not null)
            results = JsonSerializer.Deserialize<List<AllocationRunResult>>(
                row.Results, JsonCamelCase
            ) ?? [];

        return new AllocationRunStatusResponse(
            row.Id,
            row.Status,
            row.RequestedAt,
            row.StartedAt,
            row.CompletedAt,
            row.RequestedBy,
            row.Error,
            row.OrdersProcessed,
            row.OrdersFullyAllocated,
            row.OrdersPartiallyAllocated,
            results
        );
    }

    public async Task<IReadOnlyList<AllocationRunSummary>> ListRecentAsync(CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateAsync(ct);

        var rows = await conn.QueryAsync<AllocationRunSummary>(
            @"
            SELECT
                id as RunId,
                status as Status,
                requested_at as RequestedAt,
                completed_at as CompletedAt,
                orders_processed as OrdersProcessed
            FROM allocation_runs
            ORDER BY requested_at DESC
            LIMIT 20
            "
        );

        return rows.ToList();
    }

    private static readonly JsonSerializerOptions JsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    // Plain class with mutable setters so Dapper maps by property name, not by
    // constructor-parameter position. A positional record requires the SQL columns to
    // appear in exactly the same order as the constructor parameters; a mismatch
    // causes "no matching constructor" at runtime even when every name is correct.
    private class AllocationRunRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? RequestedBy { get; set; }
        public string? Error { get; set; }
        public int? OrdersProcessed { get; set; }
        public int? OrdersFullyAllocated { get; set; }
        public int? OrdersPartiallyAllocated { get; set; }
        public string? Results { get; set; }
    }
}



