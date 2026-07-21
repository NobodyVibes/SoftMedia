using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// NR-WI-006 — the full pairing flow end to end, plus the gates around it: the
/// opt-in setting, full-session-only authorization, and single-use token claims.
public class QuickConnectIntegrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record InitiateResp(string Code, string Secret, int ExpiresInSeconds, int PollIntervalSeconds);
    private record StateResp(string Status, string? AccessToken, string? RefreshToken);
    private record PendingResp(string Code, string? DeviceName, string? RequestIp, DateTime CreatedAt);
    private record MintResponse(Guid id, string token, string label);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SetEnabledAsync("true");
    }

    private async Task SetEnabledAsync(string value)
    {
        using var scope = Factory.Services.CreateScope();
        var settings = scope.ServiceProvider
            .GetRequiredService<SoftMedia.Server.Services.Infrastructure.ISettingsService>();
        var row = await settings.GetSettingAsync("EnableQuickConnect")
            ?? throw new InvalidOperationException("EnableQuickConnect not seeded");
        row.Value = value;
        await settings.UpdateSettingsAsync(new List<AppSetting> { row });
    }

    private HttpClient JwtClient(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private async Task<InitiateResp> InitiateAsync(string deviceName = "Test TV")
    {
        var resp = await Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/quickconnect/initiate", new { DeviceName = deviceName });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<InitiateResp>(JsonOpts))!;
    }

    [Fact]
    public async Task Disabled_AllEndpoints404()
    {
        await SetEnabledAsync("false");
        try
        {
            var anon = Factory.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound,
                (await anon.PostAsJsonAsync("/api/v1/quickconnect/initiate", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await anon.GetAsync("/api/v1/quickconnect/state?secret=abc")).StatusCode);

            var user = await Factory.SeedUserAsync("qc-disabled");
            var jwt = JwtClient(user);
            Assert.Equal(HttpStatusCode.NotFound,
                (await jwt.GetAsync("/api/v1/quickconnect/pending/ABC234")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await jwt.PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = "ABC234" })).StatusCode);
        }
        finally
        {
            await SetEnabledAsync("true");
        }
    }

    [Fact]
    public async Task FullPairingFlow_InitiatePendingAuthorizeClaim_ThenSingleUse()
    {
        var user = await Factory.SeedUserAsync("qc-flow");
        var device = Factory.CreateClient(); // anonymous, no cookie jar
        var init = await InitiateAsync("Living Room TV");

        // Device polls: pending until approved.
        var pending = await device.GetFromJsonAsync<StateResp>(
            $"/api/v1/quickconnect/state?secret={init.Secret}", JsonOpts);
        Assert.Equal("Pending", pending!.Status);
        Assert.Null(pending.AccessToken);

        // User reviews the device, then approves.
        var jwt = JwtClient(user);
        var review = await jwt.GetFromJsonAsync<PendingResp>(
            $"/api/v1/quickconnect/pending/{init.Code}", JsonOpts);
        Assert.Equal("Living Room TV", review!.DeviceName);

        var approve = await jwt.PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = init.Code });
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        // Device's next poll claims tokens.
        var approved = await device.GetFromJsonAsync<StateResp>(
            $"/api/v1/quickconnect/state?secret={init.Secret}", JsonOpts);
        Assert.Equal("Approved", approved!.Status);
        Assert.False(string.IsNullOrEmpty(approved.AccessToken));
        Assert.False(string.IsNullOrEmpty(approved.RefreshToken));

        // The access token works; the refresh token drives the NR-WI-005 body flow.
        var authed = Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", approved.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/v1/auth/media-token")).StatusCode);

        var refreshed = await Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh-token", new { RefreshToken = approved.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // Single-use: the secret is dead after the successful claim.
        Assert.Equal(HttpStatusCode.NotFound,
            (await device.GetAsync($"/api/v1/quickconnect/state?secret={init.Secret}")).StatusCode);
    }

    [Fact]
    public async Task Authorize_RequiresFullSession_ApiTokenRejected()
    {
        // An sm_ API token — even an admin-scoped one — must not approve devices:
        // pairing mints a FULL session, which would let a narrow token escalate.
        var user = await Factory.SeedUserAsync("qc-apitoken");
        var mintResp = await JwtClient(user).PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "qc", scopes = new[] { "read:library", "write:state" }, expiresAt = (DateTime?)null });
        mintResp.EnsureSuccessStatusCode();
        var mint = (await mintResp.Content.ReadFromJsonAsync<MintResponse>(JsonOpts))!;

        var init = await InitiateAsync();
        var tokenClient = Factory.CreateClient();
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mint.token);

        var resp = await tokenClient.PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = init.Code });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Authorize_RequiresFullSession_MediaTokenRejected()
    {
        var user = await Factory.SeedUserAsync("qc-mediatoken");
        string mediaToken;
        using (var scope = Factory.Services.CreateScope())
        {
            mediaToken = scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateMediaToken(user).Token;
        }

        var init = await InitiateAsync();
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mediaToken);

        var resp = await client.PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = init.Code });

        // Media tokens are confined to media routes at authentication time -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Authorize_UnknownCode_Is404()
    {
        var user = await Factory.SeedUserAsync("qc-unknown");
        var resp = await JwtClient(user).PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = "ZZZZZZ" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Claim_BannedAfterApproval_IsRejected()
    {
        var user = await Factory.SeedUserAsync("qc-banned");
        var init = await InitiateAsync();

        var approve = await JwtClient(user).PostAsJsonAsync("/api/v1/quickconnect/authorize", new { Code = init.Code });
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        // Ban between approval and the device's claim.
        await Factory.WithDbAsync(async db =>
        {
            var u = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstAsync(db.Users, x => x.Id == user.Id);
            u.IsBanned = true;
            await db.SaveChangesAsync();
        });

        var resp = await Factory.CreateClient().GetAsync($"/api/v1/quickconnect/state?secret={init.Secret}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
