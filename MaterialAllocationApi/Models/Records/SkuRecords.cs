using System.ComponentModel.DataAnnotations;

public record CreateSkuRequest(
    [Required, MaxLength(64)] string SkuCode,
    [Required, MaxLength(500)] string Description,
    [Range(0, int.MaxValue)] int InitialOnHand = 0
);

public record AdjustSkuRequest(
    [Required] int Delta,
    [Required, MaxLength(500)] string Reason
);

/***
`Version` is included in `SkuResponse` so callers can see the concurrency token value. 
This is useful when debugging allocation races: you can see that a row's `Version` jumped from 2 to 4, 
meaning two concurrent updates landed between your read and write.
***/
public record SkuResponse(
    Guid Id,
    string SkuCode,
    string Description,
    int OnHand,
    int Version,
    DateTime UpdatedAt
);