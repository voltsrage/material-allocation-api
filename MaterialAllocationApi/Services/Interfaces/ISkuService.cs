public interface ISkuService
{
    Task<SkuResponse> CreateAsync(CreateSkuRequest request, CancellationToken ct = default);
    Task<SkuResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<SkuResponse>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<SkuResponse> AdjustAsync(Guid id, AdjustSkuRequest request, CancellationToken ct = default);
}