using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>SKU catalog and inventory adjustment operations.</summary>
[Authorize]
[ApiController]
[Route("api/v1/skus")]
public class SkusController : ControllerBase
{
    private readonly ISkuService _skus;
    private readonly IAllocationService _allocation;

    public SkusController(ISkuService skus, IAllocationService allocation)
    {
        _skus = skus;
        _allocation = allocation;
    }

    /// <summary>Create a new SKU with an initial on-hand quantity.</summary>
    /// <response code="201">SKU created.</response>
    /// <response code="422">SKU code missing, too long, negative initial quantity, or code already exists.</response>
    [Authorize(Roles = "warehouse-ops")]
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<SkuResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]    
    public async Task<ActionResult<ApiResponse<SkuResponse>>> Create(
        CreateSkuRequest request, CancellationToken ct
    )
    {
        var sku = await _skus.CreateAsync(request, ct);

        return CreatedAtAction(nameof(GetById), new {id = sku.Id}, ApiResponse<SkuResponse>.Created(sku));
    }

    /// <summary>Get a SKU by ID.</summary>
    /// <response code="200">SKU found.</response>
    /// <response code="404">No SKU with the given ID exists.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<SkuResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<SkuResponse>>> GetById(Guid id, CancellationToken ct)
    {
        var sku = await _skus.GetByIdAsync(id, ct);

        return Ok(ApiResponse<SkuResponse>.Ok(sku));
    }

    /// <summary>List SKUs with pagination, ordered by SKU code.</summary>
    /// <response code="200">Paginated SKU list.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SkuResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<SkuResponse>>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default
    )
    {
        var result = await _skus.ListAsync(page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<SkuResponse>>.Ok(result));
    }

    /// <summary>
    /// Adjust on-hand quantity. Delta is signed: positive to add stock, negative to remove.
    /// Returns the updated SKU. Returns 409 if a concurrent modification races this request.
    /// </summary>
    /// <response code="200">Adjustment applied; returns updated SKU.</response>
    /// <response code="404">No SKU with the given ID exists.</response>
    /// <response code="409">Concurrent modification detected — re-read and retry.</response>
    /// <response code="422">Delta would drive on_hand negative, or reason is missing.</response>
    [Authorize(Roles = "warehouse-ops")]
    [HttpPost("{id:guid}/adjust")]
    [ProducesResponseType(typeof(ApiResponse<SkuResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<SkuResponse>>> Adjust(
        Guid id, AdjustSkuRequest request, CancellationToken ct
    )
    {
        var sku = await _skus.AdjustAsync(id, request, ct);
        return Ok(ApiResponse<SkuResponse>.Ok(sku));
    }

    /// <summary>
    /// Return the available quantity for a SKU.
    /// Formula: available = on_hand - reserved - allocated.
    /// on_hand is mutable (decremented on allocation), so it already excludes committed allocations.
    /// Reserved is 0 until Phase 7 adds the reservations table.
    /// </summary>
    /// <response code="200">Availability for the given SKU.</response>
    /// <response code="404">No SKU with the given ID exists.</response>
    [HttpGet("{id:guid}/availability")]
    [ProducesResponseType(typeof(ApiResponse<AvailabilityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AvailabilityResponse>>> GetAvailability(Guid id, CancellationToken ct)
    {
        var result = await _allocation.GetAvailabilityAsync(id, ct);
        return Ok(ApiResponse<AvailabilityResponse>.Ok(result));
    }
}