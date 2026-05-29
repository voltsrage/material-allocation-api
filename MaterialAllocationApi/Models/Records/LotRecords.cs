using System.ComponentModel.DataAnnotations;

public record ReceiveLotRequest
(
    [Required, MaxLength(64)] string LotCode,
    [Range(1, int.MaxValue)] int Quantity,
    DateTimeOffset? ReceivedAt = null // defaults to UtcNow in the service if not provided
);

public record LotResponse
(
    Guid Id,
    Guid SkuId,
    string SkuCode,
    string LotCode,
    int Quantity,
    int AvailableQty,
    string Status,
    DateTime ReceivedAt,
    DateTime CreatedAt
);

public record LotStatusTransitionRequest(
    [MaxLength(500)] string? Notes = null
);

// GET /api/v1/lots/{id}/allocations
public record LotAllocationHistoryEntry
(
    Guid OrderId,
    string ReferenceCode,
    string Priority,
    string OrderStatus,
    Guid OrderLineId,
    int QuantityConsumed,
    DateTime OccurredAt
);

// GET /api/v1/lots/{id}/events
public record LotEventHistoryEntry
(
    string EventType,
    int QuantityAffected,
    string? Notes,
    DateTime OccurredAt
);

public record OrderLotProvenanceEntry
(
    Guid LotId,
    string LotCode,
    Guid SkuId,
    string SkuCode,
    int QuantityConsumed,
    string LotStatus,
    DateTime ReceivedAt
);

// GET /api/v1/skus/{id}/lots/snapshot — aggregate row per status group
public record LotStatusSummary
(
    string Status,
    int LotCount,
    int TotalAvailableQty
);

// GET /api/v1/skus/{id}/lots/snapshot — individual lot row
public record LotSnapshotEntry
(
    Guid LotId,
    string LotCode,
    int Quantity,
    int AvailableQty,
    string Status,
    DateTime ReceivedAt
);

// GET /api/v1/skus/{id}/lots/snapshot — full response
public record SkuLotSnapshotResponse
(
    Guid SkuId,
    string SkuCode,
    int OnHand,
    IReadOnlyList<LotStatusSummary> Summary,
    IReadOnlyList<LotSnapshotEntry> Lots
);