using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Security audit C1/M1 regression tests:
///   • a must-change-password principal is confined to the change-password flow
///     server-side (the SPA prompt cannot be bypassed via a direct API call), and
///   • a self-registered account that still needs approval receives NO token.
public class AccountActivationSecurityTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private record AuthResponseDto(string AccessToken);

    private async Task SetSettingAsync(string key, string value)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Settings.FindAsync(key);
        if (existing != null) existing.Value = value;
        else db.Settings.Add(new AppSetting { Key = key, Value = value });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task MustChangePassword_User_IsBlockedFromApi_ButCanChangePassword()
    {
        var user = await Factory.SeedUserAsync("changeme");
        await Factory.WithDbAsync(async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == user.Id);
            u.MustChangePassword = true;
            await db.SaveChangesAsync();
        });

        var client = Factory.CreateClient();

        // Login still succeeds (it does not gate on the flag) and returns a token
        // that carries the must_change claim.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "changeme", Password = "TestPass!1" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!.AccessToken;

        // A normal protected endpoint is blocked server-side with 403.
        var blocked = await SendWithTokenAsync(client, HttpMethod.Get, "/api/v1/media/hero", token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("password_change_required", await blocked.Content.ReadAsStringAsync());

        // change-password is on the allow-list and succeeds.
        var changed = await SendWithTokenAsync(client, HttpMethod.Post, "/api/v1/auth/change-password", token,
            new { OldPassword = "TestPass!1", NewPassword = "BrandNew!Pass9" });
        changed.EnsureSuccessStatusCode();

        // After changing, a fresh login is no longer flagged and reaches the API.
        var relogin = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "changeme", Password = "BrandNew!Pass9" });
        relogin.EnsureSuccessStatusCode();
        var newToken = (await relogin.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!.AccessToken;
        var ok = await SendWithTokenAsync(client, HttpMethod.Get, "/api/v1/media/hero", newToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Signup_OpenRegistration_UnapprovedUser_GetsNoToken()
    {
        await SetSettingAsync("AllowUserSignup", "Enabled");
        var client = Factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/signup", new
        {
            Username = "pendinguser",
            Password = "StrongPass!9",
            InviteCode = (string?)null,
            FirstName = "P",
            LastName = "U",
        });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);

        // The account exists but is unapproved and has no refresh token issued.
        await Factory.WithDbAsync(async db =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == "pendinguser");
            Assert.NotNull(u);
            Assert.False(u!.IsApproved);
            Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.UserId == u.Id));
        });
    }

    [Fact]
    public async Task SeededAdmin_DoesNotUseTheKnownDefaultPassword()
    {
        // C1: the seeded admin must not be loginable with the old hardcoded default.
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "admin", Password = "admin123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWithTokenAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }
}
