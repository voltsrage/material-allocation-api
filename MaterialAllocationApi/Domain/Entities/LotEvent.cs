public class LotEvent
{
    public Guid Id { get; private set; }
    public Guid LotId { get; private set; }
    public Guid SkuId { get; private set; }
    public LotEventType EventType { get; private set; }
    public int QuantityAffected { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private LotEvent() {}
    
    public LotEvent(
        Guid lotId,
        Guid skuId,
        LotEventType eventType,
        int quantityAffected,
        string? notes
    )
    {
        LotId = lotId;
        SkuId = skuId;
        EventType = eventType;
        QuantityAffected = quantityAffected;
        Notes = notes;
        OccurredAt = DateTimeOffset.UtcNow;
    }
}