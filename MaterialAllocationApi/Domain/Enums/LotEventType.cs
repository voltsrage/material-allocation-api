public enum LotEventType
{
    Quarantined,
    Released,
    Scrapped
}

public static class LotEventTypeExtensions
{
    public static string ToDbString(this LotEventType t) => t switch
    {
        LotEventType.Quarantined => "quarantined",
        LotEventType.Released => "released",
        LotEventType.Scrapped => "scrapped",
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static LotEventType FromDbString(string value) => value switch
    {
        "quarantined" => LotEventType.Quarantined,
        "released" => LotEventType.Released,
        "scrapped" => LotEventType.Scrapped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown lot event type: '{value}'")
    };
}