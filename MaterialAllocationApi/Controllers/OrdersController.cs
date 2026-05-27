using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Order lifecycle: create, list, get, allocate, reserve, and cancel.</summary>
[Authorize]
[ApiController]
[Route("api/v1/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly IAllocationService _allocation;

    private readonly IReservationService _reservations;

    public OrdersController(IOrderService orders, IAllocationService allocation, IReservationService reservations)
    {
        _orders = orders;
        _allocation = allocation;
        _reservations = reservations;
    }

    /// <summary>Create an order with one or more lines referencing existing SKUs.</summary>
    /// <response code="201">Order created.</response>
    /// <response code="422">Reference code already exists, unknown SKU IDs, duplicate SKU in lines, invalid priority, or requested_qty ≤ 0.</response>
    [Authorize(Roles = "sales-ops")]
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<OrderResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Create(
        CreateOrderRequest request, CancellationToken ct
    )
    {
        var order = await _orders.CreateAsync(request, ct);

        return CreatedAtAction(nameof(GetById), new {id = order.Id}, ApiResponse<OrderResponse>.Created(order));
    }

    /// <summary>Get a full order including all lines and their allocation status.</summary>
    /// <response code="200">Order found.</response>
    /// <response code="404">No order with the given ID exists.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<OrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetById(Guid id, CancellationToken ct)
    {
        var order = await _orders.GetByIdAsync(id, ct);
        return Ok(ApiResponse<OrderResponse>.Ok(order));
    }

    /// <summary>List orders with optional status filter and pagination.</summary>
    /// <response code="200">Paginated order summary list.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<OrderSummaryResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<OrderSummaryResponse>>>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default
    )
    {
        var result = await _orders.ListAsync(status, page, pageSize, ct);

        return Ok(ApiResponse<PagedResult<OrderSummaryResponse>>.Ok(result));
    }

    /// <summary>
    /// Cancel an order. Returns the updated order. Returns 409 if already cancelled.
    /// Phase 6 adds allocation release — in Phase 3, only the status transition is applied.
    /// </summary>
    /// <response code="200">Order cancelled.</response>
    /// <response code="404">No order with the given ID exists.</response>
    /// <response code="409">Order is already cancelled.</response>
    [Authorize(Roles = "sales-ops")]
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<OrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<OrderResponse>>>Cancel(Guid id, CancellationToken ct)
    {
        var order = await _orders.CancelAsync(id, ct);

        return Ok(ApiResponse<OrderResponse>.Ok(order));
    }

    /// <summary>
    /// Allocate available stock to open order lines.
    /// Lines are filled in sku_id order against current on_hand.
    /// Partial allocation is allowed: unfulfilled lines appear with remainingQty > 0.
    /// Returns 409 if the order is cancelled or already fully allocated.
    /// </summary>
    /// <response code="200">Allocation applied; response shows partial or full result.</response>
    /// <response code="404">No order with the given ID exists.</response>
    /// <response code="409">Order is cancelled or already fully allocated.</response>
    [Authorize(Roles = "allocation-manager")]
    [HttpPost("{id:guid}/allocate")]
    [ProducesResponseType(typeof(ApiResponse<AllocationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AllocationResponse>>> Allocate(Guid id, CancellationToken ct)
    {
        var result = await _allocation.AllocateAsync(id, ct);
        return Ok(ApiResponse<AllocationResponse>.Ok(result));
    }

    /// <summary>
    /// Reserve available stock for each open order line for the specified TTL.
    /// Calling reserve again for the same order replaces existing reservations (TTL refresh).
    /// available = on_hand - reservations held by other orders.
    /// </summary>
    /// <response code="200">Reservation created. Lines array may be empty if no stock is available to reserve.</response>
    /// <response code="404">Order not found.</response>
    /// <response code="409">Order is cancelled or fully allocated.</response>
    [Authorize(Roles = "allocation-manager")]
    [HttpPost("{id:guid}/reserve")]
    [ProducesResponseType(typeof(ApiResponse<ReservationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ReservationResponse>>>Reserve(
        Guid id,
        ReserveRequest request,
        CancellationToken ct
    )
    {
        var result = await _reservations.ReserveAsync(id, request, ct);
        return Ok(ApiResponse<ReservationResponse>.Ok(result));
    }

    /// <summary>Returns the full allocation audit history for an order in chronological order.</summary>
    /// <response code="200">Event list (may be empty for a newly created order).</response>
    /// <response code="404">Order not found.</response>
    [HttpGet("{id:guid}/events")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AllocationEventResponse>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AllocationEventResponse>>>> GetEvents(Guid id, CancellationToken ct)
    {
        var events = await _allocation.GetEventsAsync(id, ct);
        
        return Ok(ApiResponse<IReadOnlyList<AllocationEventResponse>>.Ok(events));
    }
}