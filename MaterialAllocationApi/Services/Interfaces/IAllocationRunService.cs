public interface IAllocationRunService
{
    Task<EnqueueResult> EnqueueAsync(string? requestedBy, CancellationToken ct = default);
    Task<AllocationRunStatusResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AllocationRunSummary>> ListRecentAsync(CancellationToken ct = default);
}