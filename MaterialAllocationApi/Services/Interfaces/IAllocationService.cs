public interface  IAllocationService
{
    Task<AllocationResponse> AllocateAsync(Guid orderId, CancellationToken ct = default);
    Task<AvailabilityResponse> GetAvailabilityAsync(Guid skuId, CancellationToken ct = default);
    Task<AllocationRunResponse> RunPriorityAllocationAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AllocationEventResponse>> GetEventsAsync(Guid orderId, CancellationToken ct = default);
}