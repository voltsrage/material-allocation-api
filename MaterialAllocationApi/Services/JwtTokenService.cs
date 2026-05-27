using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public class JwtTokenService : ITokenService
{
    private readonly AuthSettings _settings;

    public JwtTokenService(IOptions<AuthSettings> settings)
    {
        _settings = settings.Value;
    }
    public string IssueToken(string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new [] {new Claim(ClaimTypes.Role, role)};

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_settings.TokenExpiryMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}