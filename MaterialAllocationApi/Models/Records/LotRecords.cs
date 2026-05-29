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