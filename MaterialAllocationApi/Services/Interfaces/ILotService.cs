public interface ILotService
{
    Task<LotResponse> ReceiveAsync(Guid skuId, ReceiveLotRequest request, CancellationToken ct = default);
    Task<LotResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<LotResponse>> ListBySkuAsync(Guid skuId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<LotResponse> QuarantineAsync(Guid id, string? notes, CancellationToken ct = default);
    Task<LotResponse> ReleaseAsync(Guid id, string? notes, CancellationToken ct = default);
    Task<LotResponse> ScrapAsync(Guid id, string? notes, CancellationToken ct = default);
}