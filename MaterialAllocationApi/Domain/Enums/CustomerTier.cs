public enum CustomerTier
{
    Tier1,
    Tier2,
    Tier3
}

public static class CustomerTierExtensions
{
    public static string ToDbString(this CustomerTier t) => t switch
    {
        CustomerTier.Tier1 => "tier-1",
        CustomerTier.Tier2 => "tier-2",
        CustomerTier.Tier3 => "tier-3",
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static CustomerTier FromDbString(string value) => value switch
    {
        "tier-1" => CustomerTier.Tier1,
        "tier-2" => CustomerTier.Tier2,
        "tier-3" => CustomerTier.Tier3,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown customer tier: '{value}'.")
    };
}

