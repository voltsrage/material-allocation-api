using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


/// <summary>
/// Development-only token endpoint. Issues a signed JWT for the requested role.
/// In production, replace with tokens issued by your identity provider.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private static readonly HashSet<string> ValidRoles =
        ["warehouse-ops", "sales-ops", "allocation-manager", "read-only"];

    private readonly ITokenService _tokens;

    public AuthController(ITokenService tokens)
    {
        _tokens = tokens;
    }

    /// <summary>Issue a JWT for the given role. Development use only.</summary>
    /// <response code="200">Token issued.</response>
    /// <response code="422">Unknown role.</response>
    [HttpPost("token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<TokenResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public ActionResult<ApiResponse<TokenResponse>> GetToken([FromBody] TokenRequest request)
    {
        if(!ValidRoles.Contains(request.Role))
            return UnprocessableEntity(
                ApiResponse<object>.Fail(422, $"Unknown role '{request.Role}'.", "VALIDATION_ERROR")
            );
        
        var token = _tokens.IssueToken(request.Role);

        return Ok(ApiResponse<TokenResponse>.Ok(new TokenResponse(token)));
    }
}