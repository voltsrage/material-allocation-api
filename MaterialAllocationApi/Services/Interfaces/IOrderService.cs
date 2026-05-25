public interface IOrderService
{
    Task<OrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken ct = default);
    Task<OrderResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<OrderSummaryResponse>> ListAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct = default);
}