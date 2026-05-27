public enum AllocationEventType
{
    AllocationCommitted, // units moved from on_hand to allocated
    AllocationReleased, // units returned to on_hand (cancel)
    ReservationCreated, // units soft-held under a reservation TTL
    ReservationReleased, // reservation explicitly released before TTL
    ReservationExpired // reservation removed by the expiry background job
}