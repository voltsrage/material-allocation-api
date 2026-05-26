using System.ComponentModel.DataAnnotations;

public record ReserveRequest(
    // 1 minute minimum; 10,080 minutes = 7 days maximum
    [Range(1, 10080)] int TtlMinutes
);

public record ReservationLineResult(
    Guid OrderLineId,
    Guid SkuId,
    string SkuCode,
    int QuantityReserved,
    DateTime ExpiresAt
);

public record ReservationResponse(
    Guid OrderId,
    string ReferenceCode,
    IReadOnlyList<ReservationLineResult> Lines,
    DateTime ExpiresAt // same for all lines in this reserve call
);