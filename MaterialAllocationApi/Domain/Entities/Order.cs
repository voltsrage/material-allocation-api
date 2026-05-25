public class Order
{
    private static readonly HashSet<string> ValidPriorities = 
    ["standard", "high", "critical"];

    public Guid Id { get; private set; }
    public string ReferenceCode  { get; private set; } = string.Empty;
    public string Priority  { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt  { get; private set; }

    public ICollection<OrderLine> Lines  { get; private set; } = new List<OrderLine>();

    private Order() {}

    public Order(string referenceCode, string priority)
    {
        if(!ValidPriorities.Contains(priority))
            throw new ArgumentException(
                $"Invalid priority '{priority}'. Valid values: {string.Join(", ", ValidPriorities)}"
            );

        ReferenceCode = referenceCode;
        Priority = priority;
        Status = "open";
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void AddLine(Guid skuId, int requestedQty)
    {
        if(requestedQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedQty), "Requested quantity must be positive");

        Lines.Add(new OrderLine(skuId, requestedQty));
    }

    public void Cancel()
    {
        if(Status == "cancelled")
            throw new InvalidOperationException("Order is already cancelled");

        Status = "cancelled";
    }

    // Called by the allocation service (Phase 4) after updating line allocated quantities
    // Reads Lines collection - caller must ensure it is loaded before calling
    public void RecomputeStatus()
    {
        if(!Lines.Any()) return;

        var totalRequested = Lines.Sum(l => l.RequestedQty);
        var totalAllocated = Lines.Sum(l => l.AllocatedQty);

        Status = totalAllocated switch
        {
            0 => "open",
            var a when a >= totalRequested => "fully_allocated",
            _ => "partially_allocated"  
        };
    }
}