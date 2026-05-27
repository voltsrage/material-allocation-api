public class OutboxMessage
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public string? Error { get; private set; }

    private OutboxMessage() {}

    public OutboxMessage(string eventType, string payload)
    {
        EventType = eventType;
        Payload = payload;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkProcessed() => ProcessedAt = DateTimeOffset.UtcNow;

    public void MarkFailed(string error)
    {
        Error = error;
        // ProcessedAt intentionally left null - message stays eligible for retry
    }
}