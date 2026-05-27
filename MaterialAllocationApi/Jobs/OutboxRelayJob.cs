
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class OutboxRelayJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxRelayJob> _logger;
    private readonly OutboxRelaySettings _settings;

    public OutboxRelayJob(
        IServiceScopeFactory scopeFactory, 
        ILogger<OutboxRelayJob> logger,
        IOptions<OutboxRelaySettings> settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Outbox relay batch failed.");
            }
        }

        await Task.Delay(
            TimeSpan.FromSeconds(_settings.IntervalSeconds),stoppingToken
        );
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var messages = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(_settings.BatchSize)
            .ToListAsync();

        if(messages.Count == 0) return;

        foreach(var message in messages)
        {
            try
            {
                await publisher.PublishAsync(message, ct);
                message.MarkProcessed();
            }
            catch (Exception ex)
            {
                
                message.MarkFailed(ex.Message);
                _logger.LogError(ex, 
                "Failed to publish outbox message {MessageId} ({EventTyp})",
                message.Id, message.EventType
                );
            }
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Outbox relay processed {Count} message(s).",
            messages.Count(m => m.ProcessedAt != null));
    }
}