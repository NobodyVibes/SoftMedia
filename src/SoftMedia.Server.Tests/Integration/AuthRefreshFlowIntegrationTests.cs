using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Todo 04 integration tests — the full auth lifecycle:
/// login → refresh rotation → logout, plus reuse detection and
/// change-password-revokes-all behaviour. These cover everything the mock-
/// based unit tests couldn't verify end-to-end.
public class AuthRefreshFlowIntegrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true,
        AllowAutoRedirect = false,
    });

    private record AuthResponseDto(string AccessToken, UserBlob User);
    private record UserBlob(Guid Id, string Username);

    private async Task<(AuthResponseDto body, string refreshCookie)> LoginAsync(
        HttpClient client, string username = "alice", string password = "TestPass!1")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = password });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!;
        var cookie = ExtractSetCookieValue(response, "refreshToken")
            ?? throw new InvalidOperationException("Login response did not set refreshToken cookie");
        return (body, cookie);
    }

    private static string? ExtractSetCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return null;
        foreach (var header in headers)
        {
            // "refreshToken=abc...; expires=...; path=/api/v1/auth/; secure; httponly; samesite=strict"
            var parts = header.Split(';', 2);
            var kv = parts[0].Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == cookieName) return kv[1];
        }
        return null;
    }

    [Fact]
    public async Task Login_IssuesAccessToken_AndRefreshCookie_AndPersistsHash()
    {
        await Factory.SeedUserAsync("alice");
        var client = NewClient();

        var (body, refreshCookie) = await LoginAsync(client);

        Assert.NotEmpty(body.AccessToken);
        Assert.NotEmpty(refreshCookie);

        // DB has a matching row, hashed — NOT equal to the raw cookie value.
        await Factory.WithDbAsync(async db =>
        {
            var tokens = await db.RefreshTokens.Where(rt => rt.User.Username == "alice").ToListAsync();
            var active = Assert.Single(tokens);
            Assert.NotEqual(refreshCookie, active.TokenHash);
            Assert.Equal(64, active.TokenHash.Length);
            Assert.Null(active.RevokedAt);
        });
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndReturnsNewAccessToken()
    {
        await Factory.SeedUserAsync("alice");
        var client = NewClient();
        var (first, _) = await LoginAsync(client);

        var response = await client.PostAsync("/api/v1/auth/refresh-token", content: null);
        response.EnsureSuccessStatusCode();

        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!;
        Assert.NotEmpty(refreshed.AccessToken);
        Assert.NotEqual(first.AccessToken, refreshed.AccessToken);

        // DB now has two rows for the user: old (revoked, ReplacedBy set) + new (active)
        await Factory.WithDbAsync(async db =>
        {
            var tokens = await db.RefreshTokens.Where(rt => rt.User.Username == "alice").OrderBy(rt => rt.CreatedAt).ToListAsync();
            Assert.Equal(2, tokens.Count);
            Assert.NotNull(tokens[0].RevokedAt);
            Assert.Equal(RefreshTokenRevocationReason.Rotated, tokens[0].ReasonRevoked);
            Assert.Equal(tokens[1].Id, tokens[0].ReplacedByTokenId);
            Assert.Null(tokens[1].RevokedAt);
        });
    }

    [Fact]
    public async Task Refresh_WithStolenReusedToken_InvalidatesEntireChain()
    {
        await Factory.SeedUserAsync("alice");
        var legitClient = NewClient();
        var (_, stolenCookie) = await LoginAsync(legitClient);

        // Legit client rotates to a new token.
        var rotate = await legitClient.PostAsync("/api/v1/auth/refresh-token", content: null);
        rotate.EnsureSuccessStatusCode();

        // Attacker presents the now-rotated-away token on a fresh client.
        var attacker = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh-token");
        req.Headers.Add("Cookie", $"refreshToken={stolenCookie}");
        var reuse = await attacker.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // The entire chain for this user is now revoked.
        await Factory.WithDbAsync(async db =>
        {
            var active = await db.RefreshTokens
                .Where(rt => rt.User.Username == "alice" && rt.RevokedAt == null)
                .CountAsync();
            Assert.Equal(0, active);

            var reuseRow = await db.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);
            Assert.NotNull(reuseRow);
        });
    }

    [Fact]
    public async Task Logout_RevokesCurrentRefreshToken_AndClearsCookie()
    {
        await Factory.SeedUserAsync("alice");
        var client = NewClient();
        await LoginAsync(client);

        var logout = await client.PostAsync("/api/v1/auth/logout", content: null);
        logout.EnsureSuccessStatusCode();

        await Factory.WithDbAsync(async db =>
        {
            var token = await db.RefreshTokens.SingleAsync(rt => rt.User.Username == "alice");
            Assert.NotNull(token.RevokedAt);
            Assert.Equal(RefreshTokenRevocationReason.Logout, token.ReasonRevoked);
        });

        // Presenting the cookie again on a subsequent refresh attempt fails.
        var afterLogout = await client.PostAsync("/api/v1/auth/refresh-token", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RevokesAllActiveRefreshTokens()
    {
        await Factory.SeedUserAsync("alice");

        // Alice logs in from two "devices"
        var c1 = NewClient();
        await LoginAsync(c1);
        var c2 = NewClient();
        var (body2, _) = await LoginAsync(c2);

        // Device 2 changes password
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new { OldPassword = "TestPass!1", NewPassword = "NewPass!2" }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body2.AccessToken);
        var change = await c2.SendAsync(req);
        change.EnsureSuccessStatusCode();

        await Factory.WithDbAsync(async db =>
        {
            var tokens = await db.RefreshTokens.Where(rt => rt.User.Username == "alice").ToListAsync();
            Assert.Equal(2, tokens.Count);
            Assert.All(tokens, t =>
            {
                Assert.NotNull(t.RevokedAt);
                Assert.Equal(RefreshTokenRevocationReason.PasswordChange, t.ReasonRevoked);
            });
        });

        // Device 1's refresh cookie is now useless
        var replay = await c1.PostAsync("/api/v1/auth/refresh-token", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refresh_NoCookie_Returns401_WithoutTouchingDb()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsync("/api/v1/auth/refresh-token", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await Factory.WithDbAsync(async db =>
        {
            var count = await db.RefreshTokens.CountAsync();
            Assert.Equal(0, count);
        });
    }
}
