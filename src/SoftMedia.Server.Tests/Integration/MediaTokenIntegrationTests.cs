using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Security audit H3 — the reduced-privilege "media" token that the SPA places in media URLs
/// instead of the full access token. It must omit the role claim, be vended only to an
/// authenticated user, be accepted ONLY on media/streaming routes, and be rejected elsewhere.
/// Also asserts the H3/L7 security response headers are emitted.
public class MediaTokenIntegrationTests : IntegrationTestBase
{
    private record MediaTokenResp(string Token, int ExpiresInMinutes);

    private string AccessToken(User user)
    {
        using var scope = Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateAccessToken(user);
    }

    private string MediaToken(User user)
    {
        using var scope = Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateMediaToken(user).Token;
    }

    private HttpClient BearerClient(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task MediaTokenEndpoint_VendsRoleOmittedMediaToken_ForAuthenticatedUser()
    {
        var user = await Factory.SeedUserAsync("media-vend");

        var resp = await BearerClient(AccessToken(user)).GetAsync("/api/v1/auth/media-token");
        resp.EnsureSuccessStatusCode();

        var body = (await resp.Content.ReadFromJsonAsync<MediaTokenResp>(
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!;
        Assert.False(string.IsNullOrEmpty(body.Token));
        Assert.True(body.ExpiresInMinutes > 0);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.Token);
        Assert.Equal(CastTokenClaims.MediaUse, jwt.Claims.First(c => c.Type == CastTokenClaims.TokenUse).Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.Role || c.Type == "role");
    }

    [Fact]
    public async Task MediaTokenEndpoint_RequiresAuthentication()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/auth/media-token");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task MediaToken_AcceptedOnMediaRoute()
    {
        var user = await Factory.SeedUserAsync("media-inscope");
        var mediaId = Guid.NewGuid();

        // Control: same route without auth is 401, so the accepted case isn't an anon fall-through.
        var anon = await Factory.CreateClient().GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Media token authenticates on a media route; the media doesn't exist => 404.
        var resp = await BearerClient(MediaToken(user)).GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MediaToken_RejectedOnNonMediaRoutes()
    {
        var user = await Factory.SeedUserAsync("media-outscope");
        var token = MediaToken(user);

        // /api/v1/users (admin API) and /api/v1/media/{id} (metadata) are NOT media routes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await BearerClient(token).GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BearerClient(token).GetAsync($"/api/v1/media/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task MediaToken_RejectedAfterUserBanned()
    {
        // Audit wave-2 L-3: a stateless media token must stop working within its lifetime once
        // the user is banned, mirroring the cast-token recheck (and complementing WS-3
        // revocation). AA-WI-011: the eligibility verdict is cached with a short TTL; the app's
        // ban endpoint (UsersController.BanUser) eagerly invalidates it, so this test — which
        // bans via a direct DB write — performs the same invalidation the endpoint does. The
        // TTL only bounds staleness for out-of-band DB edits.
        var user = await Factory.SeedUserAsync("media-banned");
        var token = MediaToken(user);

        // Works while eligible (404 = authenticated, media simply missing).
        var before = await BearerClient(token).GetAsync($"/api/v1/stream/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoftMedia.Server.Data.AppDbContext>();
            var u = await db.Users.FindAsync(user.Id);
            u!.IsBanned = true;
            await db.SaveChangesAsync();
        }
        Factory.Services.GetRequiredService<SoftMedia.Server.Services.Identity.IUserEligibilityCache>()
            .Invalidate(user.Id);

        var after = await BearerClient(token).GetAsync($"/api/v1/stream/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // ── WS-6 T6.1/T6.4/T6.7 — query-token rejection + media-token read-only ──

    [Fact]
    public async Task FullAccessToken_InQueryString_IsRejectedOnMediaRoutes()
    {
        // T6.1 (M-2): query strings leak into logs/proxies/history — a full
        // role-bearing access token must be header-or-nothing.
        var user = await Factory.SeedUserAsync("ws6-queryjwt");
        var access = AccessToken(user);
        var client = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/stream/{Guid.NewGuid()}?token={access}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/music/album/{Guid.NewGuid()}/cover?access_token={access}")).StatusCode);

        // Control: the SAME access token still authenticates via the Authorization
        // header on the same route (404 = authed, media simply missing).
        Assert.Equal(HttpStatusCode.NotFound,
            (await BearerClient(access).GetAsync($"/api/v1/stream/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task MediaToken_InQueryString_StillAuthenticates()
    {
        // The whole point of the media token: it is the ONE thing allowed in a query
        // string (browsers can't set headers on <img>/<video>).
        var user = await Factory.SeedUserAsync("ws6-querymedia");
        var client = Factory.CreateClient();

        var resp = await client.GetAsync($"/api/v1/stream/{Guid.NewGuid()}?token={MediaToken(user)}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // authed; media missing
    }

    [Fact]
    public async Task MediaToken_IsRejectedOnWrites()
    {
        // T6.4 (L-4/L-5): media tokens are GET/HEAD-only — the book bookmark writes and
        // transcode session mutations live under media-route prefixes, so a leaked
        // media URL must not be able to mutate state.
        var user = await Factory.SeedUserAsync("ws6-mediawrite");
        var media = MediaToken(user);
        var client = Factory.CreateClient();

        // Query-string form on a transcode mutation.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.DeleteAsync($"/api/transcode/{Guid.NewGuid()}?sid=abc&token={media}")).StatusCode);
        // Header form on a book write.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await BearerClient(media).PostAsJsonAsync($"/api/v1/books/{Guid.NewGuid()}/bookmarks",
                new { position = "1", label = "x" })).StatusCode);

        // Control: GET with the same token on the same prefix still authenticates.
        Assert.Equal(HttpStatusCode.NotFound,
            (await BearerClient(media).GetAsync($"/api/v1/stream/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task MediaTokenMint_RequiresReadLibraryScope_ForApiTokens()
    {
        // WS-6/B-18 interaction: a media token grants GET access to the content routes,
        // so minting one must require read:library — otherwise an unscoped API token
        // launders itself into the very access the scope enforcement denied.
        var user = await Factory.SeedUserAsync("ws6-mint");
        var jwt = BearerClient(AccessToken(user));

        var mintResp = await jwt.PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "ws6", scopes = new[] { ApiTokenScopes.WriteState }, expiresAt = (DateTime?)null });
        mintResp.EnsureSuccessStatusCode();
        var writeOnly = (await mintResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await BearerClient(writeOnly).GetAsync("/api/v1/auth/media-token")).StatusCode);

        // A full session still mints (asserted by MediaTokenEndpoint_VendsRoleOmittedMediaToken).
    }

    [Fact]
    public async Task SecurityHeaders_AreEmitted()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/media/hero"); // 401, headers still present
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", resp.Headers.GetValues("X-Frame-Options").Single());

        // Audit wave-2 WS-13: CSP ships report-only by default (Security:EnforceCsp unset in tests),
        // so it is observed but never blocks — and the enforcing header must NOT be present.
        Assert.True(resp.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.False(resp.Headers.Contains("Content-Security-Policy"));
        var csp = resp.Headers.GetValues("Content-Security-Policy-Report-Only").Single();
        Assert.Contains("default-src 'self'", csp);
        // The Google Cast SDK (cast_sender.js) loads from gstatic — the policy must allow it so an
        // enforcing CSP doesn't break casting (verified against the built index.html).
        Assert.Contains("script-src 'self' https://www.gstatic.com", csp);
    }
}
