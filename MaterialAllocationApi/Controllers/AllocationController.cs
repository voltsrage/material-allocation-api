using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Global priority-aware allocation run across all open orders.</summary>
[Authorize]
[ApiController]
[Route("api/v1/allocations")]
public class AllocationController : ControllerBase
{
    private readonly IAllocationService _allocations;
    private readonly IAllocationRunService _runs;

    public AllocationController(
        IAllocationService allocations, 
        IAllocationRunService runs)
    {
        _allocations = allocations;
        _runs = runs;
    }

    /// <summary>
    /// Enqueue a priority-aware allocation run. Returns 202 immediately.
    /// Poll GET /api/v1/allocations/runs/{runId} for status and results.
    /// Returns 409 if a run is already pending or running.
    /// </summary>
    /// <response code="202">Run accepted. Response contains the run ID to poll.</response>
    /// <response code="409">A run is already pending or running.</response>
    [HttpPost("run")]
    [Authorize(Roles = "allocation-manager")]
    [ProducesResponseType(typeof(ApiResponse<AllocationRunAcceptedResponse>), 202)]
    [ProducesResponseType(typeof(ApiResponse<object>), 409)]
    public async Task<IActionResult> EnqueueRun(CancellationToken ct)
    {
        var requestedBy = User.Identity?.Name;
        var result = await _runs.EnqueueAsync(requestedBy, ct);

        return result.Match(
            run => StatusCode(202, ApiResponse<AllocationRunAcceptedResponse>.Created(
                new AllocationRunAcceptedResponse(run.Run.Id))),
            conflict => Conflict(ApiResponse<object>.Fail(409, 
                $"A run is already {conflict.Status}. Poll run {conflict.Id} for status.",
                           "RUN_IN_PROGRESS"
            ))
        );
    }

    /// <summary>Get the status and results of an allocation run by ID.</summary>
    /// <response code="200">Run found.</response>
    /// <response code="404">No run with the given ID exists.</response>
    [HttpGet("runs/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AllocationRunStatusResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<AllocationRunStatusResponse>>> GetRun(Guid id, CancellationToken ct)
    {
        var run = await _runs.GetByIdAsync(id, ct);
        return Ok(ApiResponse<AllocationRunStatusResponse>.Ok(run));
    }

    /// <summary>List the most recent allocation runs, newest first.</summary>
    /// <response code="200">Run list (may be empty).</response>
    [HttpGet("runs")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AllocationRunSummary>>), 200)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AllocationRunSummary>>>> ListRuns(CancellationToken ct)
    {
        var runs = await _runs.ListRecentAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<AllocationRunSummary>>.Ok(runs));
    }
}