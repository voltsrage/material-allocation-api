public interface ILotService
{
    Task<LotResponse> ReceiveAsync(Guid skuId, ReceiveLotRequest request, CancellationToken ct = default);
    Task<LotResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<LotResponse>> ListBySkuAsync(Guid skuId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<LotResponse> QuarantineAsync(Guid id, string? notes, CancellationToken ct = default);
    Task<LotResponse> ReleaseAsync(Guid id, string? notes, CancellationToken ct = default);
    Task<LotResponse> ScrapAsync(Guid id, string? notes, CancellationToken ct = default);
    Task<PagedResult<LotAllocationHistoryEntry>> GetAllocationsAsync(Guid lotId, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<LotEventHistoryEntry>> GetEventsAsync(Guid lotId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderLotProvenanceEntry>> GetOrderLotsAsync(Guid orderId, CancellationToken ct);
    Task<SkuLotSnapshotResponse> GetSkuSnapshotAsync(Guid skuId, CancellationToken ct);
}