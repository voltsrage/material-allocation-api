public class IdempotencySettings
{
    public int ExpiryHours {get;set;} = 24;
    public int CleanupIntervalSeconds {get;set;} = 3600;
    public int StuckProcessingAgeMinutes {get;set;} = 5;
}