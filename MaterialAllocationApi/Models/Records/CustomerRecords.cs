using System.ComponentModel.DataAnnotations;

public record CreateCustomerRequest
(
    [Required,MaxLength(64)] string CustomerCode,
    [Required, MaxLength(256)] string Name,
    [Required] CustomerTier Tier
);

public record CustomerResponse
(
    Guid Id,
    string CustomerCode,
    string Name,
    string Tier,
    DateTime CreatedAt
);