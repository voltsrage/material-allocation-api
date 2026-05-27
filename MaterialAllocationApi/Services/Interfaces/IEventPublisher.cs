public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct = default);
}