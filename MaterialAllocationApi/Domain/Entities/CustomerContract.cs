public class CustomerContract
{
    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Customer Customer { get; private set; } = null!;
    public Guid SkuId { get; private set; }
    public Sku Sku { get; private set; } = null!;
    public int FloorQty { get; private set; } // minimum units guaranteed per allocation run
    public int? CeilingQty { get; private set; } // maximum units null = uncapped
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; } // null = open-ended
    public DateTimeOffset CreatedAt { get; private set; }

    private CustomerContract() {}

    public CustomerContract(
        Guid customerId,
        Guid skuId,
        int floorQty,
        int? ceilingQty,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo
    )
    {
        if(floorQty < 0)
            throw new ValidationException("floor_qty mush be >= 0.");
        if(ceilingQty.HasValue && ceilingQty.Value  < floorQty)
            throw new ValidationException("ceiling_qty must be >= floor_qty");
        if(effectiveTo.HasValue && effectiveTo.Value < effectiveFrom)
            throw new ValidationException("effective_to must be >= effective_from.");

        CustomerId = customerId;
        SkuId = skuId;
        FloorQty = floorQty;
        CeilingQty = ceilingQty;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsActiveOn(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo == null || date <= EffectiveTo);
}