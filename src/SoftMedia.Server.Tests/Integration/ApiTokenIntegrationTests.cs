using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// End-to-end coverage for per-user API tokens (P1-WI-002): minting, using an sm_
/// token against a protected endpoint, scope enforcement (read-only token blocked on
/// a write), revocation, admin-scope restriction, and hash-only persistence.
public class ApiTokenIntegrationTests : IntegrationTestBase
{
    private record MintResponse(Guid id, string token, string label);

    private HttpClient JwtClient(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private static HttpClient ApiTokenClient(SoftMediaWebApplicationFactory factory, string rawToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private async Task<MintResponse> MintAsync(HttpClient client, params string[] scopes)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "test", scopes, expiresAt = (DateTime?)null });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MintResponse>())!;
    }

    [Fact]
    public async Task Mint_ReturnsRawToken_WithSmPrefix()
    {
        var user = await Factory.SeedUserAsync("tokuser1");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);

        Assert.StartsWith("sm_", mint.token);
        Assert.NotEqual(Guid.Empty, mint.id);
    }

    [Fact]
    public async Task ApiToken_AuthenticatesAgainstProtectedEndpoint_AndUpdatesLastUsed()
    {
        var user = await Factory.SeedUserAsync("tokuser2");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadState);

        // /account/api-tokens (GET) requires only [Authorize] — any authenticated principal.
        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.GetAsync("/api/v1/account/api-tokens");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var lastUsed = await Factory.WithDbAsync(db =>
            db.ApiTokens.Where(t => t.Id == mint.id).Select(t => t.LastUsedAt).FirstAsync());
        Assert.NotNull(lastUsed);
    }

    [Fact]
    public async Task Anonymous_Is401_OnScopeGatedWriteEndpoint()
    {
        // Regression: the write:state scope policy must FAIL CLOSED for anonymous
        // requests (it previously fail-opened because the handler treated "no scope
        // claim" as a full session — an auth bypass).
        var anon = Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/interaction/{Guid.NewGuid()}/watchlist", new { isWatchlisted = true });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyToken_Is403_OnWriteEndpoint()
    {
        var user = await Factory.SeedUserAsync("tokuser3");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        // Interaction endpoints require write:state — a read:library token must be 403.
        var resp = await tokenClient.PostAsJsonAsync(
            $"/api/v1/interaction/{Guid.NewGuid()}/rate", new { rating = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task WriteToken_PassesScopeGate_OnWriteEndpoint()
    {
        var user = await Factory.SeedUserAsync("tokuser4");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.WriteState);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        // watchlist validates media existence and returns a clean 404 for a missing id,
        // so a 404 here proves the write:state scope gate was passed (not 403/401).
        var resp = await tokenClient.PostAsJsonAsync(
            $"/api/v1/interaction/{Guid.NewGuid()}/watchlist", new { isWatchlisted = true });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RevokedToken_Is401_OnNextUse()
    {
        var user = await Factory.SeedUserAsync("tokuser5");
        var jwt = JwtClient(user);
        var mint = await MintAsync(jwt, ApiTokenScopes.ReadState);

        var del = await jwt.DeleteAsync($"/api/v1/account/api-tokens/{mint.id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.GetAsync("/api/v1/account/api-tokens");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotMintAdminScope()
    {
        var user = await Factory.SeedUserAsync("tokuser6", role: UserRole.User);
        var resp = await JwtClient(user).PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "x", scopes = new[] { ApiTokenScopes.Admin }, expiresAt = (DateTime?)null });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_CanMintAdminScope_AndReachAdminEndpoint()
    {
        var admin = await Factory.SeedUserAsync("tokadmin", role: UserRole.Admin);
        var mint = await MintAsync(JwtClient(admin), ApiTokenScopes.Admin);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.GetAsync("/api/v1/admin/backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task AdminMintedReadOnlyToken_Is403_OnAdminEndpoint()
    {
        // R-WI-006/D-5: a NON-admin-scoped token — even one minted by an admin — must not
        // carry the admin role claim, so it can no longer satisfy [Authorize(Roles="Admin")].
        // Previously the role claim was emitted unconditionally (a privilege escalation).
        var admin = await Factory.SeedUserAsync("tokadmin-ro", role: UserRole.Admin);
        var mint = await MintAsync(JwtClient(admin), ApiTokenScopes.ReadLibrary);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.GetAsync("/api/v1/admin/backup");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyToken_Is403_OnPlaylistCreate()
    {
        // R-WI-006: playlist creation now requires write:state (previously plain [Authorize],
        // so any token could mutate). A read-only token must be 403.
        var user = await Factory.SeedUserAsync("tokuser-pl");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.PostAsJsonAsync("/api/v1/playlists", new { name = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_CannotMintAnotherToken()
    {
        // R-WI-006 / diff-review CRITICAL: token administration is full-session only. Even a
        // write:state token must be 403 when trying to mint — otherwise a leaked read/write
        // token trivially escalates itself by minting a higher-scoped (or admin) token,
        // making the rest of the scope enforcement illusory.
        var user = await Factory.SeedUserAsync("tokuser-mint");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.WriteState);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "escalate", scopes = new[] { ApiTokenScopes.WriteState }, expiresAt = (DateTime?)null });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyToken_Is403_OnWebhookCreate()
    {
        // R-WI-006 / diff-review HIGH: a read-only token must not register an outbound webhook
        // (data-exfil + SSRF surface). Authorization fails before body binding, so the request
        // shape is irrelevant to the 403.
        var user = await Factory.SeedUserAsync("tokuser-wh");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);

        var tokenClient = ApiTokenClient(Factory, mint.token);
        var resp = await tokenClient.PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "library.scan.completed" } });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── B-18: the read scopes are ENFORCED, not decorative ───────────────────
    // Before B-18, catalog and state reads were plain [Authorize], so ANY token
    // could read all media metadata / user state regardless of its scopes.

    [Fact]
    public async Task WriteStateOnlyToken_Is403_OnCatalogReads()
    {
        var user = await Factory.SeedUserAsync("tokuser-b18a");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.WriteState);
        var tokenClient = ApiTokenClient(Factory, mint.token);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync("/api/v1/media/search?query=anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync("/api/v1/libraries")).StatusCode);
        // Content too — gating search while leaving /stream open would be backwards.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync($"/api/v1/stream/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ReadLibraryToken_PassesCatalogReads()
    {
        var user = await Factory.SeedUserAsync("tokuser-b18b");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);
        var tokenClient = ApiTokenClient(Factory, mint.token);

        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/media/search?query=anything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/libraries")).StatusCode);
    }

    [Fact]
    public async Task ReadLibraryToken_Is403_OnStateReads()
    {
        var user = await Factory.SeedUserAsync("tokuser-b18c");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);
        var tokenClient = ApiTokenClient(Factory, mint.token);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync("/api/v1/playlists")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync("/api/v1/watchlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tokenClient.GetAsync("/api/v1/continue-watching")).StatusCode);
    }

    [Fact]
    public async Task ReadStateToken_PassesStateReads()
    {
        var user = await Factory.SeedUserAsync("tokuser-b18d");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadState);
        var tokenClient = ApiTokenClient(Factory, mint.token);

        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/playlists")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/watchlist")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/continue-watching")).StatusCode);
    }

    [Fact]
    public async Task AdminScopeToken_GrantsEveryScope()
    {
        var admin = await Factory.SeedUserAsync("tokadmin-b18", role: UserRole.Admin);
        var mint = await MintAsync(JwtClient(admin), ApiTokenScopes.Admin);
        var tokenClient = ApiTokenClient(Factory, mint.token);

        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/media/search?query=anything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await tokenClient.GetAsync("/api/v1/playlists")).StatusCode);
    }

    [Fact]
    public async Task JwtSession_IsUnaffected_ByScopeEnforcement()
    {
        // Scopes only constrain API tokens — a full session passes every scope policy.
        var user = await Factory.SeedUserAsync("tokuser-b18e");
        var jwt = JwtClient(user);

        Assert.Equal(HttpStatusCode.OK,
            (await jwt.GetAsync("/api/v1/media/search?query=anything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await jwt.GetAsync("/api/v1/playlists")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await jwt.GetAsync("/api/v1/watchlist")).StatusCode);
    }

    [Fact]
    public async Task RawToken_IsNeverPersisted_OnlyHash()
    {
        var user = await Factory.SeedUserAsync("tokuser7");
        var mint = await MintAsync(JwtClient(user), ApiTokenScopes.ReadLibrary);

        var stored = await Factory.WithDbAsync(db =>
            db.ApiTokens.Where(t => t.Id == mint.id).Select(t => t.TokenHash).FirstAsync());

        Assert.DoesNotContain("sm_", stored);
        Assert.NotEqual(mint.token, stored);
        Assert.Equal(64, stored.Length); // hex SHA-256
    }
}
