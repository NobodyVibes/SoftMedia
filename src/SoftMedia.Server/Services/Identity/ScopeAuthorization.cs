using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// Requires either a full-session principal (browser JWT / cookie login — which is
/// inherently unrestricted) OR an API-token principal that carries the named scope.
///
/// The asymmetry is intentional: scopes only constrain API tokens. A logged-in user
/// already has full rights for their role, so JWT-authenticated requests satisfy
/// every scope requirement without carrying scope claims.
/// </summary>
public class ScopeRequirement : IAuthorizationRequirement
{
    public string Scope { get; }
    public ScopeRequirement(string scope) => Scope = scope;
}

public class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        // FAIL CLOSED for anonymous: a scope policy must never authorize an
        // unauthenticated principal. (An anonymous principal carries no scope claim,
        // so without this guard the "no scope claim ⇒ full session ⇒ Succeed" branch
        // below would let anonymous requests through — an auth bypass.) Returning
        // without Succeed leaves the requirement unmet so the policy fails.
        if (context.User.Identity is not { IsAuthenticated: true })
            return Task.CompletedTask;

        // API-token principals are authenticated under the ApiToken scheme and carry
        // "scope" claims; full-session principals are authenticated under JwtBearer
        // and carry none. Identify an API-token principal by the presence of any
        // scope claim — if it's a token, it must hold the required scope; otherwise
        // (JWT session) the requirement is satisfied.
        var isApiToken = context.User.HasClaim(c => c.Type == ApiTokenAuthenticationHandler.ScopeClaimType);

        if (!isApiToken)
        {
            // Full session (or admin scope, which grants everything — see below).
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var hasScope = context.User.HasClaim(ApiTokenAuthenticationHandler.ScopeClaimType, requirement.Scope)
                       || context.User.HasClaim(ApiTokenAuthenticationHandler.ScopeClaimType, Models.ApiTokenScopes.Admin);

        if (hasScope) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class ScopePolicies
{
    public const string ReadLibrary = "scope:read:library";
    public const string ReadState = "scope:read:state";
    public const string WriteState = "scope:write:state";

    /// Registers a policy per scope. Each REQUIRES an authenticated user (defense in
    /// depth alongside the handler's anonymous guard — a named policy does NOT inherit
    /// the default policy's RequireAuthenticatedUser, so it must be stated explicitly),
    /// then layers the scope requirement that only constrains API tokens.
    public static void AddScopePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(ReadLibrary, p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Models.ApiTokenScopes.ReadLibrary)));
        options.AddPolicy(ReadState, p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Models.ApiTokenScopes.ReadState)));
        options.AddPolicy(WriteState, p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Models.ApiTokenScopes.WriteState)));
    }
}
