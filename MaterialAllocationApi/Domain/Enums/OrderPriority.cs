public enum OrderPriority
{
    Standard,
    High,
    Critical
}

public static class OrderPriorityExtensions
{
    public static string ToDbString(this OrderPriority priority) => priority switch
    {
        OrderPriority.Standard => "standard",
        OrderPriority.High     => "high",
        OrderPriority.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };

    public static OrderPriority FromDbString(string value) => value switch
    {
        "standard" => OrderPriority.Standard,
        "high"     => OrderPriority.High,
        "critical" => OrderPriority.Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown order priority: '{value}'")
    };
}
