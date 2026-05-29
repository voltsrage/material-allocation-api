public class Lot
{
    public Guid Id{ get; private set; }
    public Guid SkuId { get; private set; }
    public Sku Sku { get; private set; } = null!;
    public string LotCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; } // total units received - never changes after intake
    public int AvailableQty { get; private set; } // units currently available; decremented by allocation and quarantine
    public LotStatus Status { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Lot() {}
    
    public Lot(Guid skuId, string lotCode, int quantity, DateTimeOffset receivedAt)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Lot quantity must be positive.");

        SkuId = skuId;
        LotCode = lotCode;
        Quantity = quantity;
        AvailableQty = quantity;
        Status = LotStatus.Available;
        ReceivedAt = receivedAt;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public int Quarantine()
    {
        if(Status != LotStatus.Available)
        {
            throw new InvalidOperationException(
                $"Only Available lots can be quarantined. Current status: {Status.ToDbString()}"
            );
        }

        if(AvailableQty <= 0)
            throw new InvalidOperationException("Lot has no available units to quarantine.");

        Status = LotStatus.Quarantined;
        return AvailableQty; // caller decrements sku.OnHand by this amount
    }

    public int Release()
    {
        if(Status != LotStatus.Quarantined)
            throw new InvalidOperationException(
                $"Only Quarantined lots can be released. Current status: {Status.ToDbString()}"
            );
        
        Status = LotStatus.Available;
        return AvailableQty; // caller increments sku.OnHand by this amount
    }

    public int Scrap()
    {
        if(Status == LotStatus.Scrapped)
            throw new InvalidOperationException("Lot is already scrapped,");

        if(Status == LotStatus.Depleted)
            throw new InvalidOperationException(
                "Cannot scrap a depleted lot - all units have already been allocated"
            );

        // If Available: sku.OnHand must be decremented by AvailableQty.
        // If Quarantined: sku.OnHand was already decremented at quarantine time- no further change

        var onHandImpact = Status == LotStatus.Available ? AvailableQty : 0;

        AvailableQty = 0;
        Status = LotStatus.Scrapped;

        return onHandImpact; // caller decrements sku.OnHand by this amount (may be 0)
    }
}