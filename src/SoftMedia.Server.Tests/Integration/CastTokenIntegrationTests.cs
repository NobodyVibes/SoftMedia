using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// CC-WI-003 — Chromecast stream tokens. A cast token is a long-lived, single-media JWT the
/// receiver carries in the stream URL (it can't refresh the short-lived session JWT). It must
/// carry the scope claims, accept ONLY that media's stream/transcode routes (never elsewhere,
/// even for an admin), expire, honour a per-request ban/disable, and not self-renew.
public class CastTokenIntegrationTests : IntegrationTestBase
{
    private string CastToken(User user, Guid mediaId)
    {
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return tokens.GenerateCastToken(user, mediaId);
    }

    /// Hand-built cast token whose `exp` is well past the JwtBearer clock skew (5 min).
    private string ExpiredCastToken(Guid userId, Guid mediaId)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Factory.JwtSecret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "SoftMediaServer", audience: "SoftMediaClient",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(CastTokenClaims.TokenUse, CastTokenClaims.CastUse),
                new Claim(CastTokenClaims.CastMedia, mediaId.ToString()),
            },
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient BearerClient(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task CastToken_CarriesScopeClaims_AndUsesConfiguredTtl()
    {
        var user = await Factory.SeedUserAsync("caster", role: UserRole.User);
        var mediaId = Guid.NewGuid();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(CastToken(user, mediaId));

        Assert.Equal(CastTokenClaims.CastUse, jwt.Claims.First(c => c.Type == CastTokenClaims.TokenUse).Value);
        Assert.Equal(mediaId.ToString(), jwt.Claims.First(c => c.Type == CastTokenClaims.CastMedia).Value);
        Assert.Equal(user.Id.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        // Defense in depth: a cast token must NOT carry the role claim.
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.Role || c.Type == "role");
        // TTL is driven by JwtSettings:CastTokenExpiryHours (the test factory sets 9h), proving
        // config is read and it's the long cast window, not the 15-minute access-token window.
        var ttl = jwt.ValidTo - DateTime.UtcNow;
        Assert.True(ttl > TimeSpan.FromHours(8) && ttl < TimeSpan.FromHours(10), $"TTL was {ttl}");
    }

    [Fact]
    public async Task CastToken_RejectedOnAdminEndpoint_EvenForAdmin()
    {
        // Minted for an admin, but the cast scope must strip ALL non-stream access — so this is
        // 401 (authentication fails), not 403. (Doubly safe now that the role claim is dropped.)
        var admin = await Factory.SeedUserAsync("cast-admin", role: UserRole.Admin);

        var resp = await BearerClient(CastToken(admin, Guid.NewGuid())).GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CastToken_RejectedOnDifferentMediaStreamRoute()
    {
        var user = await Factory.SeedUserAsync("cast-cross", role: UserRole.User);
        var token = CastToken(user, Guid.NewGuid()); // scoped to media A

        var resp = await BearerClient(token).GetAsync($"/api/v1/stream/{Guid.NewGuid()}"); // media B

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CastToken_AcceptedOnItsOwnStreamRoute()
    {
        var user = await Factory.SeedUserAsync("cast-inscope", role: UserRole.User);
        var mediaId = Guid.NewGuid();

        // No auth on the same route => 401, proving the route is genuinely auth-gated (so the
        // accepted case below is not an anonymous fall-through).
        var anon = await Factory.CreateClient().GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // The in-scope cast token authenticates; the media doesn't exist, so the handler returns
        // exactly 404 — accepted by auth, resolved to a real principal, then not found.
        var resp = await BearerClient(CastToken(user, mediaId)).GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ExpiredCastToken_IsRejected_OnItsOwnStreamRoute()
    {
        var user = await Factory.SeedUserAsync("cast-expired", role: UserRole.User);
        var mediaId = Guid.NewGuid();

        var resp = await BearerClient(ExpiredCastToken(user.Id, mediaId)).GetAsync($"/api/v1/stream/{mediaId}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CastToken_ForBannedUser_IsRejected_PerRequest()
    {
        var user = await Factory.SeedUserAsync("cast-banned", role: UserRole.User);
        var mediaId = Guid.NewGuid();
        var token = CastToken(user, mediaId);

        // Accepted while the account is healthy (404 = auth passed, media absent).
        var before = await BearerClient(token).GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        // Ban the user; the SAME cast token must now be rejected on its next request.
        // AA-WI-011: the eligibility verdict is cached with a short TTL and eagerly
        // invalidated by the app's ban endpoint (UsersController.BanUser); this direct
        // DB write performs the same invalidation the endpoint does.
        await Factory.WithDbAsync(async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == user.Id);
            u.IsBanned = true;
            await db.SaveChangesAsync();
        });
        Factory.Services.GetRequiredService<SoftMedia.Server.Services.Identity.IUserEligibilityCache>()
            .Invalidate(user.Id);

        var after = await BearerClient(token).GetAsync($"/api/v1/stream/{mediaId}");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task CastToken_CannotMintAnotherCastToken()
    {
        var user = await Factory.SeedUserAsync("cast-renew", role: UserRole.User);
        var mediaId = Guid.NewGuid();

        // The plan endpoint is within the cast token's own scope, but minting must be refused.
        var resp = await BearerClient(CastToken(user, mediaId)).PostAsJsonAsync(
            $"/api/transcode/{mediaId}/plan?cast=true",
            new { videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" }, supportedContainers = new[] { "hls" }, maxResolution = 1080, maxBitrate = 0, maxAudioChannels = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
