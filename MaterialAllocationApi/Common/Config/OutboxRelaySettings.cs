public class OutboxRelaySettings
{
    public int IntervalSeconds {get; set;} = 5;
    public int BatchSize {get;set;} = 50;
}