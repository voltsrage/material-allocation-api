using System.ComponentModel.DataAnnotations;

public record CreateContractRequest
(
    [Required] Guid SkuId,
    [Range(0, int.MaxValue)] int FloorQty,
    int? CeilingQty, // null = uncapped
    [Required] DateOnly EffectiveFrom,
    DateOnly? EffectiveTo // null = open-ended
);

public record ContractResponse
(
    Guid Id,
    Guid CustomerId,
    Guid SkuId,
    string SkuCode,
    int FloorQty,
    int? CeilingQty,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTime CreatedAt
);

public record ContractUtilizationResponse(
    Guid ContractId,
    Guid SkuId,
    string SkuCode,
    int FloorQty,
    int? CeilingQty,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    int AllocatedQty // total allocated on this customer's non-cancelled order lines for this SKU
);