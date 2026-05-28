public class Order
{
    public Guid Id { get; private set; }
    public string ReferenceCode { get; private set; } = string.Empty;
    public OrderPriority Priority { get; private set; }
    public OrderStatus Status { get; private set; }
    public Guid? CustomerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public Customer? Customer { get; private set; }

    public ICollection<OrderLine> Lines { get; private set; } = new List<OrderLine>();

    private Order() {}

    public Order(string referenceCode, OrderPriority priority, Guid? customerId)
    {
        ReferenceCode = referenceCode;
        Priority = priority;
        Status = OrderStatus.Open;
        CustomerId = customerId;
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
        if(Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Order is already cancelled");

        Status = OrderStatus.Cancelled;
    }

    // Called by the allocation service after updating line allocated quantities.
    // Reads Lines collection — caller must ensure it is loaded before calling.
    public void RecomputeStatus()
    {
        if(!Lines.Any()) return;

        var totalRequested = Lines.Sum(l => l.RequestedQty);
        var totalAllocated = Lines.Sum(l => l.AllocatedQty);

        Status = totalAllocated switch
        {
            0                              => OrderStatus.Open,
            var a when a >= totalRequested => OrderStatus.FullyAllocated,
            _                              => OrderStatus.PartiallyAllocated
        };
    }
}
