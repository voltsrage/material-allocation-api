public interface IReservationService
{
    Task<ReservationResponse> ReserveAsync(Guid orderId, ReserveRequest request, CancellationToken ct = default);
    Task ReleaseAsync(Guid reservationId, CancellationToken ct = default);
    Task<int> ExpireAsync(CancellationToken ct = default);
}