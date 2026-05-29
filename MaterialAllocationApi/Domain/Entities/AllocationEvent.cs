public class AllocationEvent
{
    public Guid Id { get; private set; }
    public AllocationEventType EventType { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OrderLineId { get; private set; }
    public Guid SkuId { get; private set; }
    public int Quantity { get; private set; }
    public Guid? LotId {get; private set;}
    public DateTimeOffset OccurredAt { get; private set; }

    private AllocationEvent() {}

    public AllocationEvent(
        AllocationEventType eventType,
        Guid orderId,
        Guid orderLineId,
        Guid skuId,
        int quantity,
        Guid? lotId = null
    )
    {
        EventType = eventType;
        OrderId = orderId;
        OrderLineId = orderLineId;
        SkuId = skuId;
        Quantity = quantity;
        LotId = lotId;
        OccurredAt = DateTimeOffset.UtcNow;
    }
}