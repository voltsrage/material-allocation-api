public class AuthSettings
{
    public string JwtSecret {get; set;} = string.Empty;
    public string Issuer {get;set;} = "material-allocation-api";
    public string Audience {get;set;} = "material-allocation-clients";
    public int TokenExpiryMinutes {get; set;} = 60;
}