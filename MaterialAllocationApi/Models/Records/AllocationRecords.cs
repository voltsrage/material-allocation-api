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