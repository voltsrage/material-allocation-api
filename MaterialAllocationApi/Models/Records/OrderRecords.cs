using System.ComponentModel.DataAnnotations;

public record CreateOrderLineRequest(
    [Required] Guid SkuId,
    [Range(1, int.MaxValue)] int RequestedQty
);

public record CreateOrderRequest(
    [Required, MaxLength(64)] string ReferenceCode,
    [Required] OrderPriority Priority,
    [Required, MinLength(1)] IReadOnlyList<CreateOrderLineRequest> Lines
);

public record OrderLineResponse(
    Guid Id,
    Guid SkuId,
    string SkuCode,
    int RequestedQty,
    int AllocatedQty
);

public record OrderResponse(
    Guid Id,
    string ReferenceCode,
    string Priority,
    string Status,
    DateTime CreatedAt,
    IReadOnlyList<OrderLineResponse> Lines
);

public record OrderSummaryResponse(
    Guid Id,
    string ReferenceCode,
    string Priority,
    string Status,
    long LineCount,
    DateTime CreatedAt
);