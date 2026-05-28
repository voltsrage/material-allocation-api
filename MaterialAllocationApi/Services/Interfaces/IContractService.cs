public interface IContractService
{
    Task<ContractResponse> CreateAsync(Guid customerId, CreateContractRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ContractResponse>> ListAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<ContractUtilizationResponse>> GetUtilizationAsync(Guid customerId, CancellationToken ct = default);
}