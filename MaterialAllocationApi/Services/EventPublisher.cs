
public class LoggingEventPublisher : IEventPublisher
{
    private readonly ILogger<LoggingEventPublisher> _logger;

    public LoggingEventPublisher(ILogger<LoggingEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Outbox: publishing {EventType} message {MessageId} — {Payload}",
            message.EventType, message.Id, message.Payload);
        return Task.CompletedTask;
    }
}