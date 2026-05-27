using Microsoft.AspNetCore.Mvc;

public record AllocationLineResult(
    Guid SkuId,
    string SkuCode,
    int RequestedQty,
    int AllocatedQty, // total allocated across all runs (cumulative, not just this run)
    int RemainingQty // RequestedQty - AllocatedQty after this run
);

public record AllocationResponse(
    Guid OrderId,
    string Status, // "partially_allocated", "fully_allocated"
    bool IsFullyAllocated,
    IReadOnlyList<AllocationLineResult> Lines
);

public record AvailabilityResponse(
    Guid SkuId,
    string SkuCode,
    int OnHand,
    int Reserved, // 0 until Phase 7 adds the reservations table
    int Available // OnHand, Reserved; on_hand already reflects committed allocations
);

public record AllocationRunResponse(
    int OrdersProcessed,
    int OrdersFullyAllocated,
    int OrdersPartiallyAllocated,
    IReadOnlyList<AllocationRunResult> Results
);
public record AllocationRunResult
(
    Guid OrderId,
    string ReferenceCode,
    string Priority,
    string Status,
    bool IsFullyAllocated
);

public record AllocationEventResponse(
    Guid Id,
    string EventType,
    Guid OrderLineId,
    Guid SkuId,
    int Quantity,
    DateTime OccurredAt
);

// Returned immediately by POST /allocations/run
public record AllocationRunAcceptedResponse(Guid RunId);

// Returned by GET /allocations/runs/{id}
public record AllocationRunStatusResponse(
    Guid RunId,
    string Status,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? RequestedBy,
    string? Error,
    int? OrdersProcessed,
    int? OrdersFullyAllocated,
    int? OrdersPartiallyAllocated,
    IReadOnlyList<AllocationRunResult>? Results
);

// Returned by GET /allocations/runs (list)
// Parameter order must match the SQL column order in ListRecentAsync — Dapper
// matches positional-record constructors by name AND position.
public record AllocationRunSummary
(
    Guid RunId,
    string Status,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    int? OrdersProcessed
);

public record EnqueueResult
{
    public record Accepted(AllocationRun Run) : EnqueueResult;
    public record Conflict(Guid Id, string Status) : EnqueueResult;

    public IActionResult Match(
        Func<Accepted, IActionResult> onAccepted,
        Func<Conflict, IActionResult> onConflict
    ) => this switch
    {
        Accepted a => onAccepted(a),
        Conflict c => onConflict(c),
        _ => throw new InvalidOperationException()
    };
}