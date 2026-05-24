/***
`ConflictException` carries a `Code` field so callers can distinguish `CONCURRENT_MODIFICATION` from future conflict types (e.g. `ORDER_ALREADY_CANCELLED` in Phase 6) without parsing the message string.
***/

public class ConflictException : Exception
{
    public string Code {get;}
    public ConflictException(string message, string code) : base(message)
    {
        Code = code;
    }
}