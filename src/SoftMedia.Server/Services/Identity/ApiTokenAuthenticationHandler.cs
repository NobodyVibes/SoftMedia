using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// Authenticates opaque "sm_*" API tokens passed as <c>Authorization: Bearer sm_…</c>.
///
/// This is a SEPARATE scheme from JwtBearer on purpose. An sm_ token has no JWT
/// structure, so it must never reach the JwtBearer handler's validator (which would
/// reject it). The policy scheme in AddIdentityServices forwards sm_-prefixed
/// requests here and everything else to JwtBearer.
///
/// On success the principal carries:
///   - ClaimTypes.NameIdentifier = user id (so ClaimsPrincipal.GetUserId() works)
///   - ClaimTypes.Role           = the user's role (so [Authorize(Roles=...)] works
///                                  for admin-scoped tokens)
///   - "scope" claims            = the token's granted scopes (for scope policies)
/// </summary>
public class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiToken";
    public const string ScopeClaimType = "scope";

    private readonly IApiTokenService _apiTokens;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiTokenService apiTokens)
        : base(options, logger, encoder)
    {
        _apiTokens = apiTokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var raw = header[bearer.Length..].Trim();
        if (!raw.StartsWith(IApiTokenService.Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var ip = Context.Connection.RemoteIpAddress?.ToString();
        var result = await _apiTokens.AuthenticateAsync(raw, ip, Context.RequestAborted);
        if (result == null)
            return AuthenticateResult.Fail("Invalid or expired API token.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
            new(ClaimTypes.Name, result.User.Username),
            new(ClaimTypes.Role, result.User.Role.ToString()),
        };
        foreach (var scope in result.Scopes)
            claims.Add(new Claim(ScopeClaimType, scope));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
