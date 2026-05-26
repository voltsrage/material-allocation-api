public interface IRollupService
{
    Task<PagedResult<SkuShortageResponse>> GetSkuShortageAsync(
        int page, int pageSize, CancellationToken ct = default
    );
}