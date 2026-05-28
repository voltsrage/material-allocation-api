public interface ICustomerService
{
    Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken ct =default);
    Task<CustomerResponse> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<CustomerResponse>> ListAsync(int page, int pageSize, CancellationToken ct = default);
}