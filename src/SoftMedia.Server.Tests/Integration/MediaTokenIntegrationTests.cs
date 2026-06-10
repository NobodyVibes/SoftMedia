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
    public async Task SecurityHeaders_AreEmitted()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/media/hero"); // 401, headers still present
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", resp.Headers.GetValues("X-Frame-Options").Single());
    }
}
