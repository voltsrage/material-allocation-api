using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public class OutboxLagHealthCheck : IHealthCheck
{
    private readonly AllocationDbContext _db;
    private static readonly TimeSpan LagThreshold = TimeSpan.FromMinutes(2);

    public OutboxLagHealthCheck(AllocationDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var oldest = await _db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if(oldest is null)
            return HealthCheckResult.Healthy("No unprocessed outbox messages");

        var lag = DateTimeOffset.UtcNow - oldest.Value;

        return lag > LagThreshold
            ? HealthCheckResult.Degraded(
                $"Oldest unprocessed outbox message is {lag.TotalMinutes:F1} mins old,"
            ) : HealthCheckResult.Healthy($"Outbox lag: {lag.TotalSeconds:F0}s.");
    }
}