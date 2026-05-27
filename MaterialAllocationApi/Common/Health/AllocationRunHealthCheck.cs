using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public class AllocationRunHealthCheck : IHealthCheck
{
    private readonly AllocationDbContext _db;
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(15);

    public AllocationRunHealthCheck(AllocationDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var stuckRun = await _db.AllocationRuns
            .Where(r => r.Status == "running"
                && r.StartedAt < DateTimeOffset.UtcNow - StuckThreshold)
            .OrderBy(r => r.StartedAt)
            .Select(r => new {r.Id, r.StartedAt})
            .FirstOrDefaultAsync(ct);

        if(stuckRun is null)
            return HealthCheckResult.Healthy("No stuck allocation runs.");

        var age = DateTimeOffset.UtcNow - stuckRun.StartedAt!.Value;
        return HealthCheckResult.Degraded(
            $"Allocation run {stuckRun.Id} has been running for {age.TotalMinutes:F0} min. Worker may be stuck,"
        );
    }
}