public class IdempotencyRecord
{
    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestPath { get; private set; } = string.Empty;
    public string RequestMethod { get; private set; } = string.Empty;
    public string Status { get; private set; } = "processing";
    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    private IdempotencyRecord() {}

    public IdempotencyRecord(
        string idempotencyKey,
        string requestPath,
        string requestMethod,
        DateTimeOffset expiresAt
    )
    {
        IdempotencyKey = idempotencyKey;
        RequestPath = requestPath;
        RequestMethod = requestMethod;
        Status = "processing";
        CreatedAt = DateTimeOffset.UtcNow;
        ExpiresAt = expiresAt;
    }

    public void Complete(int responseStatus, string responseBody)
    {
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        Status = "complete";
    }
}