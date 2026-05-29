using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1")]
[Authorize]
public class LotsController : ControllerBase
{
    private readonly ILotService _lots;

    public LotsController(ILotService lots)
    {
        _lots = lots;
    }

    /// <summary>
    /// Receive a new lot for a SKU. Increments SKU on-hand by the lot quantity atomically.
    /// For lot-tracked SKUs this is the preferred path for adding inventory over POST /skus/{id}/adjust.
    /// </summary>
    /// <response code="201">Lot received and on-hand updated.</response>
    /// <response code="404">SKU not found.</response>
    /// <response code="422">Lot code already exists, quantity not positive, or lot code missing.</response>
    [HttpPost("skus/{skuId:guid}/lots")]
    [Authorize(Roles = "warehouse-ops")]
    [ProducesResponseType(typeof(ApiResponse<LotResponse>), 201)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<ActionResult<ApiResponse<LotResponse>>> Receive(Guid skuId, ReceiveLotRequest request, CancellationToken ct)
    {
        var lot = await _lots.ReceiveAsync(skuId, request, ct);

        return StatusCode(201, ApiResponse<LotResponse>.Created(lot));
    }

    /// <summary>
    /// List lots for a SKU, ordered most recently received first.
    /// Optional status filter: available | quarantined | depleted | scrapped.
    /// </summary>
    /// <response code="200">Paginated lot list (may be empty).</response>
    /// <response code="404">SKU not found.</response>
    [HttpGet("skus/{skuId:guid}/lots")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<LotResponse>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<PagedResult<LotResponse>>>> ListBySku(
        Guid skuId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default
    )
    {
        var result = await _lots.ListBySkuAsync(skuId, status, page, pageSize, ct);

        return Ok(ApiResponse<PagedResult<LotResponse>>.Ok(result));
    }

    /// <summary>Get a lot by ID.</summary>
    /// <response code="200">Lot found.</response>
    /// <response code="404">No lot with the given ID exists.</response>
    [HttpGet("lots/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<LotResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<LotResponse>>> GetById(Guid id, CancellationToken ct)
    {
        var lot = await _lots.GetByIdAsync(id, ct);

        return Ok(ApiResponse<LotResponse>.Ok(lot));
    }

    /// <summary>
    /// Place a lot under quality hold. Removes available units from on-hand pool.
    /// The lot's available_qty is preserved so release restores exactly the held quantity.
    /// </summary>
    /// <response code="200">Lot quarantined; returns updated lot state.</response>
    /// <response code="404">Lot not found.</response>
    /// <response code="409">Lot is not in Available status.</response>
    [HttpPost("lots/{id:guid}/quarantine")]
    [Authorize(Roles = "warehouse-ops")]
    [ProducesResponseType(typeof(ApiResponse<LotResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 409)]
    public async Task<ActionResult<ApiResponse<LotResponse>>> Quarantine(
        Guid id, LotStatusTransitionRequest? request, CancellationToken ct
    )
    {
        var lot = await _lots.QuarantineAsync(id, request?.Notes, ct);

        return Ok(ApiResponse<LotResponse>.Ok(lot));
    }

    /// <summary>
    /// Release a quarantined lot back to Available. Restores available units to on-hand pool.
    /// </summary>
    /// <response code="200">Lot released; returns updated lot state.</response>
    /// <response code="404">Lot not found.</response>
    /// <response code="409">Lot is not in Quarantined status.</response>
    [HttpPost("lots/{id:guid}/release")]
    [Authorize(Roles = "warehouse-ops")]
    [ProducesResponseType(typeof(ApiResponse<LotResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 409)]
    public async Task<ActionResult<ApiResponse<LotResponse>>> Release(
        Guid id, LotStatusTransitionRequest? request, CancellationToken ct
    )
    {
        var lot = await _lots.ReleaseAsync(id, request?.Notes, ct);

        return Ok(ApiResponse<LotResponse>.Ok(lot));
    }

    /// <summary>
    /// Permanently scrap a lot. Irreversible. If Available, removes units from on-hand.
    /// If Quarantined, on-hand is unchanged (already deducted at quarantine time).
    /// </summary>
    /// <response code="200">Lot scrapped; returns updated lot state.</response>
    /// <response code="404">Lot not found.</response>
    /// <response code="409">Lot is already scrapped or depleted.</response>
    [HttpPost("lots/{id:guid}/scrap")]
    [Authorize(Roles = "warehouse-ops")]
    [ProducesResponseType(typeof(ApiResponse<LotResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 409)]
    public async Task<ActionResult<ApiResponse<LotResponse>>> Scrap(
        Guid id, LotStatusTransitionRequest? request, CancellationToken ct
    )
    {
        var lot = await _lots.ScrapAsync(id, request?.Notes, ct);

        return Ok(ApiResponse<LotResponse>.Ok(lot));
    }

    /// <summary>
    /// Paginated history of allocation events that consumed from this lot.
    /// Each entry is one allocation_committed event — an order that was allocated
    /// in multiple runs may appear multiple times.
    /// Returns empty results (not 404) when the lot exists but has never been allocated against.
    /// </summary>
    /// <response code="200">Allocation history (may be empty).</response>
    /// <response code="404">Lot not found.</response>
    [HttpGet("lots/{id:guid}/allocations")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<LotAllocationHistoryEntry>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<PagedResult<LotAllocationHistoryEntry>>>> GetAllocations(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _lots.GetAllocationsAsync(id, page, pageSize, ct);
        return Ok(ApiResponse<PagedResult<LotAllocationHistoryEntry>>.Ok(result));
    }

    /// <summary>
    /// Full lifecycle event history for a lot — quarantine, release, and scrap events in
    /// chronological order. Each entry corresponds to one row in lot_events.
    /// </summary>
    /// <response code="200">Event list (empty if the lot has had no status transitions).</response>
    /// <response code="404">Lot not found.</response>
    [HttpGet("lots/{id:guid}/events")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<LotEventHistoryEntry>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<LotEventHistoryEntry>>>> GetEvents(Guid id, CancellationToken ct)
    {
        var result = await _lots.GetEventsAsync(id, ct);
        return Ok(ApiResponse<IReadOnlyList<LotEventHistoryEntry>>.Ok(result));
    }

    /// <summary>
    /// Lots that were consumed to fill an order, with quantities and current lot status.
    /// Returns empty results (not 404) when the order exists but was filled from non-lot
    /// inventory (the on-hand fallthrough path).
    /// </summary>
    /// <response code="200">Lot provenance list (may be empty).</response>
    /// <response code="404">Order not found.</response>
    [HttpGet("orders/{orderId:guid}/lots")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OrderLotProvenanceEntry>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OrderLotProvenanceEntry>>>> GetOrderLots(Guid orderId, CancellationToken ct)
    {
        var result = await _lots.GetOrderLotsAsync(orderId, ct);
        return Ok(ApiResponse<IReadOnlyList<OrderLotProvenanceEntry>>.Ok(result));
    }

    /// <summary>
    /// Live lot inventory snapshot for a SKU. Returns per-status aggregate counts
    /// and the full individual lot list, newest received first.
    /// The on_hand field reflects the SKU's maintained aggregate (sum of available lot quantities
    /// plus any pre-lot inventory added via adjust).
    /// </summary>
    /// <response code="200">Snapshot retrieved.</response>
    /// <response code="404">SKU not found.</response>
    [HttpGet("skus/{skuId:guid}/lots/snapshot")]
    [ProducesResponseType(typeof(ApiResponse<SkuLotSnapshotResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<SkuLotSnapshotResponse>>> GetSkuSnapshot(Guid skuId, CancellationToken ct)
    {
        var result = await _lots.GetSkuSnapshotAsync(skuId, ct);
        return Ok(ApiResponse<SkuLotSnapshotResponse>.Ok(result));
    }
}