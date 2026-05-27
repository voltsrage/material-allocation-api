using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Operational read models: shortage detection across the SKU catalog.</summary>
[Authorize]
[ApiController]
[Route("api/v1/rollup")]
public class RollupController : ControllerBase
{
    private readonly IRollupService _rollup;

    public RollupController(IRollupService rollup)
    {
        _rollup = rollup;
    }

    /// <summary>
    /// SKUs where open unfulfilled demand exceeds available stock (on_hand minus active reservations).
    /// Open demand = sum of (requested_qty - allocated_qty) across lines of open and partially-allocated orders.
    /// Results ordered by shortage descending (worst shortages first), then sku_code.
    /// </summary>
    /// <response code="200">Paged list of short SKUs. Empty when no SKU is short.</response>
    [HttpGet("sku-shortages")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SkuShortageResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<SkuShortageResponse>>>> GetSkuShortages(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default
    )
    {
        var result = await _rollup.GetSkuShortageAsync(page, pageSize,ct);
        return Ok(ApiResponse<PagedResult<SkuShortageResponse>>.Ok(result));
    }
}