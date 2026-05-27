
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public class AllocationRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AllocationRunWorker> _logger;
    private readonly TimeSpan _interval;

    public AllocationRunWorker(
        IServiceScopeFactory scopeFactory, 
        ILogger<AllocationRunWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(
            config.GetValue("AllocationRunWorker:PollIntervalSeconds", 5)
        );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Allocation run worker started. Poll interval: {Interval}s.", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                await ProcessNextAsync(scope, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Allocation run worker encountered an error.");
            }
        }
    }

    private async Task ProcessNextAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();

        // Claim one pending run atomically. SKIP LOCKED means a second worker instance
        // (in a multi-replica deployment) will not block on a run already claimed by
        // another instance - it simply finds no row and returns
        await using var claimTx = await db.Database.BeginTransactionAsync(ct);

        var run = await db.AllocationRuns
            .FromSqlRaw(
                @"
                SELECT * FROM allocation_runs
                WHERE status = 'pending'
                ORDER BY requested_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                "
            ).FirstOrDefaultAsync(ct);

        if(run is null)
        {
            await claimTx.RollbackAsync(ct);
            return;
        }

        run.MarkRunning();
        await db.SaveChangesAsync(ct);
        await claimTx.CommitAsync(ct);

        _logger.LogInformation("Allocation run {RunId} started", run.Id);

        var allocationService = scope.ServiceProvider.GetRequiredService<IAllocationService>();

        try
        {
            var response = await allocationService.RunPriorityAllocationAsync(ct);

            var serializedResults = JsonSerializer.Serialize(
                response.Results,
                new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase}
            );

            await using var completedTx = await db.Database.BeginTransactionAsync(ct);
            var tracked = await db.AllocationRuns.FindAsync([run.Id], ct);
            tracked!.Complete(response, serializedResults);

            await db.SaveChangesAsync(ct);
            await completedTx.CommitAsync(ct);

            _logger.LogInformation(
                "Allocation run {RunId} completed: {Processed} orders processed, {FullyAllocated} fully allocated.",
                run.Id, response.OrdersProcessed, response.OrdersFullyAllocated);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Allocation run {RunId} failed.", run.Id);
            await using var failedTx = await db.Database.BeginTransactionAsync(ct);
            var tracked = await db.AllocationRuns.FindAsync([run.Id], ct);
            tracked!.Fail(ex.Message);
            
            await db.SaveChangesAsync(ct);
            await failedTx.CommitAsync(ct);
        }
    }
}