using Microsoft.AspNetCore.Mvc;

/// <summary>Global priority-aware allocation run across all open orders.</summary>
[ApiController]
[Route("api/v1/allocations")]
public class AllocationController : ControllerBase
{
    private readonly IAllocationService _allocations;

    public AllocationController(IAllocationService allocations)
    {
        _allocations = allocations;
    }

    /// <summary>
    /// Process all open orders in priority order (critical → high → standard).
    /// Each order is allocated in its own transaction. Returns a summary of outcomes.
    /// </summary>
    /// <response code="200">Run complete. Check per-order results for individual outcomes.</response>
    [HttpPost("run")]
    [ProducesResponseType(typeof(ApiResponse<AllocationRunResponse>), 200)]
    public async Task<ActionResult<ApiResponse<AllocationRunResponse>>> RunAllocation(CancellationToken ct)
    {
        var result = await _allocations.RunPriorityAllocationAsync(ct);

        return Ok(ApiResponse<AllocationRunResponse>.Ok(result));
    }
}