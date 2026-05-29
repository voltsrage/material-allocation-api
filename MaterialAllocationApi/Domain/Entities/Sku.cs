public class Sku
{
    public Guid Id { get; private set; }
    public string SkuCode { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public int OnHand { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public ICollection<InventoryAdjustment> Adjustments { get; private set; } = new List<InventoryAdjustment>();
    public ICollection<Lot> Lots { get; private set; } = new List<Lot>();

    private Sku() {}

    public Sku(string skuCode, string description, int initialOnHand)
    {
        if(initialOnHand < 0)
            throw new ArgumentOutOfRangeException(nameof(initialOnHand), "Initial on_hand cannot be negative.");

        SkuCode = skuCode;
        Description = description;
        OnHand = initialOnHand;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public InventoryAdjustment Adjust(int delta, string reason)
    {
        var projected = OnHand + delta;
        if(projected < 0)
            throw new InvalidOperationException($"Adjustment of {delta} would reduce on_hand below zero (current: {OnHand}).");

        OnHand = projected;
        UpdatedAt = DateTimeOffset.UtcNow;

        return new InventoryAdjustment(Id, delta, reason);
    }

    public void AllocateUnits(int quantity)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity),
            "Quantity must be positive.");

        if(OnHand < quantity)
            throw new InvalidOperationException($"Insufficient stock: requested {quantity}, available {OnHand}.");

        OnHand -= quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ReleaseUnits(int quantity)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity),
            "Quantity must be positive.");

        OnHand += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ReceiveLot(int quantity)
    {
        if(quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive");

        OnHand += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void HoldForQuarantine(int quantity)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive");

        if(OnHand < quantity)
            throw new InvalidOperationException(
                $"Cannot quarantine {quantity} units; on_hand is only {OnHand}"
            );
        
        OnHand -= quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RestoreFromQuarantine(int quantity)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive");

        OnHand += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

