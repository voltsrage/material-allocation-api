
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class IdempotencyCleanupJob : BackgroundService
{
    private readonly IServiceProvider _scopeFactory;
    private readonly ILogger<IdempotencyCleanupJob> _logger;
    private readonly IdempotencySettings _settings;

    public IdempotencyCleanupJob(
        IServiceProvider scopeFactory, 
        ILogger<IdempotencyCleanupJob> logger,
        IOptions<IdempotencySettings> settings
        )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Idempotency cleanup job started. Interval: {Interval}s.",
            _settings.CleanupIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.CleanupIntervalSeconds),stoppingToken
            );

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
                
                await CleanupAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Idempotency cleanup job encountered an error.");
            }
        }
    }

    private async Task CleanupAsync(AllocationDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stuckBefore = now.AddMinutes(-_settings.StuckProcessingAgeMinutes);

        // Delete expired complete records and stuck processing records in one query
        var deleted = await db.Database.ExecuteSqlRawAsync(
            @"
            DELETE FROM idempotency_keys
            WHERE (status = 'complete' AND expires_at <= {0})
                OR (status = 'processing' AND created_at <= {1})
            ",
            now, stuckBefore
        );

        if(deleted > 0)
            _logger.LogInformation(
                "Idempotency cleanup: deleted {Count} record(s).", deleted);
    }
}