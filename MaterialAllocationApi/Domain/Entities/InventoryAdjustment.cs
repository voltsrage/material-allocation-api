public class InventoryAdjustment
{
    public Guid Id { get; private set; }
    public Guid SkuId { get; private set; }
    public int Delta { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public Sku Sku { get; private set; } = null!;

    public InventoryAdjustment() {}

    public InventoryAdjustment(Guid skuId, int delta, string reason)
    {
        SkuId = skuId;
        Delta = delta;
        Reason = reason;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}