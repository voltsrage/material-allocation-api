using Microsoft.AspNetCore.Mvc;

/// <summary>Explicit reservation release before TTL expiry.</summary>
[ApiController]
[Route("api/v1/reservations")]
public class ReservationsController: ControllerBase
{
    private readonly IReservationService _reservations;

    public ReservationsController(IReservationService reservations)
    {
        _reservations = reservations;
    }

    /// <summary>Explicitly release a reservation before it expires.</summary>
    /// <response code="204">Reservation released.</response>
    /// <response code="404">No reservation with the given ID exists.</response>
    [HttpPost("{id:guid}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        await _reservations.ReleaseAsync(id, ct);
        return NoContent();
    }
}