public class OrderLine
{
    public Guid Id  { get; private set; }
    public Guid OrderId  { get; private set; }
    public Guid SkuId  { get; private set; }
    public int RequestedQty  { get; private set; }
    public int AllocatedQty { get; private set; }

    public Order Order  { get; private set; } = null!;
    public Sku Sku  { get; private set; } = null!;

    private OrderLine() {}

    internal OrderLine(Guid skuId, int requestedQty)
    {
        SkuId = skuId;
        RequestedQty = requestedQty;
        AllocatedQty = 0;
    }

    // Called by the allocation service(Phase 4) inside the allocation transaction
    public void Allocated(int quantity)
    {
        if(quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Allocation quantity must be positive.");

        if(AllocatedQty + quantity > RequestedQty)
            throw new InvalidOperationException(
                $"Cannot allocated {quantity} units: only {RequestedQty - AllocatedQty} remaining on this line."
            );


        AllocatedQty += quantity;
    }

    // Called by the cancel service (Phase 6) to return units to inventory before cancelling.
    public void ReleasedAllocation()
    {
        AllocatedQty = 0;
    }
}