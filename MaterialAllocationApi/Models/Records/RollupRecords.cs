public record SkuShortageResponse(
    Guid Id,
    string SkuCode,
    string Description,
    int OnHand,
    int Reserved, // active reservations reducing availability
    int Available, // OnHand - Reserved
    int OpenDemand, // sum of unfulfilled line quantities across open and partially-allocated orders
    int Shortage // OpenDemand - Available; always > 0 for rows return by this endpoint
);