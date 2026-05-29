public enum LotStatus
{
    Available,
    Quarantined,
    Depleted,
    Scrapped
}

public static class LotStatusExtensions
{
    public static string ToDbString(this LotStatus s) => s switch
    {
        LotStatus.Available => "available",
        LotStatus.Quarantined => "quarantined",
        LotStatus.Depleted => "depleted",
        LotStatus.Scrapped => "scrapped",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static LotStatus FromDbString(string value) => value switch
    {
        "available" => LotStatus.Available,
        "quarantined" => LotStatus.Quarantined,
        "depleted" => LotStatus.Depleted,
        "scrapped" => LotStatus.Scrapped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown lot status: '{value}'.")
    };
}