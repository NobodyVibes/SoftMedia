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
///   - ClaimTypes.Role           = the user's role — emitted ONLY for admin-scoped
///                                  tokens, so a non-admin-scoped token can never
///                                  satisfy [Authorize(Roles="Admin")] (D-5/R-WI-006)
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
        };

        // D-5/R-WI-006: emit the role claim ONLY for admin-scoped tokens. It was previously
        // emitted unconditionally, so a read-only token minted by an admin still satisfied
        // [Authorize(Roles="Admin")] — a full privilege escalation. The value is the user's
        // live role, so a demoted admin's admin-scoped token no longer reads as Admin either.
        if (result.Scopes.Contains(ApiTokenScopes.Admin))
            claims.Add(new Claim(ClaimTypes.Role, result.User.Role.ToString()));

        foreach (var scope in result.Scopes)
            claims.Add(new Claim(ScopeClaimType, scope));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
