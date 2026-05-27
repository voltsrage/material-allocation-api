public static class AllocationEventTypeExtensions
{
    public static string ToDbString(this AllocationEventType t) => t switch
    {
        AllocationEventType.AllocationCommitted => "allocation_committed",
        AllocationEventType.AllocationReleased => "allocation_released",
        AllocationEventType.ReservationCreated => "reservation_created",
        AllocationEventType.ReservationReleased => "reservation_released",
        AllocationEventType.ReservationExpired => "reservation_expired",
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static AllocationEventType FromDbString(string value) => value switch
    {
        "allocation_committed" => AllocationEventType.AllocationCommitted,
        "allocation_released" => AllocationEventType.AllocationReleased,
        "reservation_created" => AllocationEventType.ReservationCreated,
        "reservation_released" => AllocationEventType.ReservationReleased,
        "reservation_expired" => AllocationEventType.ReservationExpired,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown event type: '{value}")
    };
}