public class AllocationRun
{
    public Guid Id { get; private set; }
    public string Status { get; private set; } = "pending";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? RequestedBy { get; private set; }
    public string? Error { get; private set; }
    public int? OrdersProcessed { get; private set; }
    public int? OrdersFullyAllocated { get; private set; }
    public int? OrdersPartiallyAllocated { get; private set; }
    public string? Results { get; private set; } // serialized JSON array

    private AllocationRun() {}

    public AllocationRun(string? requestedBy)
    {
        RequestedBy = requestedBy;
        Status = "pending";
        RequestedAt = DateTimeOffset.UtcNow;
    }

    public void MarkRunning()
    {
        Status = "running";
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void Complete(AllocationRunResponse response, string serializedResults)
    {
        Status = "completed";
        CompletedAt = DateTimeOffset.UtcNow;
        OrdersProcessed = response.OrdersProcessed;
        OrdersFullyAllocated = response.OrdersFullyAllocated;
        OrdersPartiallyAllocated = response.OrdersPartiallyAllocated;
        Results = serializedResults;
    }

    public void Fail(string error)
    {
        Status = "failed";
        CompletedAt = DateTimeOffset.UtcNow;
        Error = error;
    }
}