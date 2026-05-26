public enum OrderStatus
{
    Open,
    PartiallyAllocated,
    FullyAllocated,
    Cancelled
}

public static class OrderStatusExtensions
{
    public static string ToDbString(this OrderStatus status) => status switch
    {
        OrderStatus.Open               => "open",
        OrderStatus.PartiallyAllocated => "partially_allocated",
        OrderStatus.FullyAllocated     => "fully_allocated",
        OrderStatus.Cancelled          => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static OrderStatus FromDbString(string value) => value switch
    {
        "open"                 => OrderStatus.Open,
        "partially_allocated"  => OrderStatus.PartiallyAllocated,
        "fully_allocated"      => OrderStatus.FullyAllocated,
        "cancelled"            => OrderStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown order status: '{value}'")
    };
}
