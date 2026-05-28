using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/customers/{customerId:guid}/contracts")]
[Authorize]
public class ContractsController : ControllerBase
{
    private readonly IContractService _contracts;

    public ContractsController(IContractService contracts)
    {
        _contracts = contracts;
    }

    /// <summary>Create a new contract for a customer and SKU.</summary>
    /// <response code="201">Contract created.</response>
    /// <response code="404">Customer or SKU not found.</response>
    /// <response code="422">Period overlaps an existing contract, invalid floor/ceiling, or date range.</response>
    [HttpPost]
    [Authorize(Roles = "sales-ops")]
    [ProducesResponseType(typeof(ApiResponse<ContractResponse>), 201)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<ActionResult<ApiResponse<ContractResponse>>> Create(
        Guid customerId, CreateContractRequest request, CancellationToken ct
    )
    {
        var contract = await _contracts.CreateAsync(customerId, request, ct);
        return StatusCode(201, ApiResponse<ContractResponse>.Created(contract));
    }

    /// <summary>List all contracts for a customer, most recent first.</summary>
    /// <response code="200">Contract list (may be empty).</response>
    /// <response code="404">Customer not found.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ContractResponse>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ContractResponse>>>> List(Guid customerId, CancellationToken ct)
    {
        var contracts = await _contracts.ListAsync(customerId, ct);

        return Ok(ApiResponse<IReadOnlyList<ContractResponse>>.Ok(contracts));
    }

    /// <summary>
    /// Live utilization snapshot for all active contracts.
    /// Shows allocated_qty against floor and ceiling for the current date.
    /// </summary>
    /// <response code="200">Utilization list (empty if no active contracts).</response>
    /// <response code="404">Customer not found.</response>
    [HttpGet("utilization")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ContractUtilizationResponse>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ContractUtilizationResponse>>>> GetUtilization(Guid customerId, CancellationToken ct)
    {
        var utilization = await _contracts.GetUtilizationAsync(customerId, ct);
        return Ok(ApiResponse<IReadOnlyList<ContractUtilizationResponse>>.Ok(utilization));
    }
}