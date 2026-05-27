using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";
    public const string RoleHeader = "X-Test-Role";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) {}

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var roleValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = roleValues
            .SelectMany(r => r!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(r => new Claim(ClaimTypes.Role, r.Trim()))
            .Append(new Claim(ClaimTypes.Name, "test-user"))
            .ToArray();

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}