using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// NR-WI-005 — the cookie-free auth flow for native clients: login with
/// TokenDelivery="body" returns the refresh token in the response (no Set-Cookie),
/// and /auth/refresh-token + /auth/logout accept the token in a JSON body. All
/// existing protections (rotation, reuse-detection chain revocation) must apply
/// identically to the body path, and the browser cookie flow must be unchanged.
public class BodyRefreshFlowIntegrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record AuthResponseDto(string AccessToken, string? RefreshToken, UserBlob User);
    private record UserBlob(Guid Id, string Username);

    private HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = false, // native-style client: NO cookie jar
        AllowAutoRedirect = false,
    });

    private static bool HasRefreshCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var headers)
        && headers.Any(h => h.TrimStart().StartsWith("refreshToken=") && !h.Contains("refreshToken=;"));

    private async Task<AuthResponseDto> BodyLoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = username, Password = "TestPass!1", TokenDelivery = "body" });
        response.EnsureSuccessStatusCode();
        Assert.False(HasRefreshCookie(response), "body-delivery login must not set the refresh cookie");
        var body = (await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!;
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
        return body;
    }

    [Fact]
    public async Task Login_WithBodyDelivery_ReturnsRefreshTokenInBody_NoCookie()
    {
        await Factory.SeedUserAsync("native-login");
        var body = await BodyLoginAsync(NewClient(), "native-login");
        Assert.NotEmpty(body.AccessToken);
    }

    [Fact]
    public async Task Login_WithoutBodyDelivery_KeepsCookieFlow_AndOmitsBodyToken()
    {
        await Factory.SeedUserAsync("cookie-login");
        var response = await NewClient().PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "cookie-login", Password = "TestPass!1" });
        response.EnsureSuccessStatusCode();

        Assert.True(HasRefreshCookie(response), "default login must keep the refresh cookie");
        // The wire shape for browsers is unchanged: no refreshToken field at all.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refreshToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_WithBodyToken_Rotates_ReturnsNewTokenInBody_NoCookie()
    {
        await Factory.SeedUserAsync("native-rotate");
        var client = NewClient();
        var login = await BodyLoginAsync(client, "native-rotate");

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new { RefreshToken = login.RefreshToken });
        response.EnsureSuccessStatusCode();
        Assert.False(HasRefreshCookie(response), "body-sourced refresh must not set a cookie");

        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!;
        Assert.False(string.IsNullOrEmpty(refreshed.RefreshToken));
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken); // rotated
        Assert.NotEmpty(refreshed.AccessToken);
    }

    [Fact]
    public async Task Refresh_ReusedBodyToken_TripsReuseDetection_AndRevokesChain()
    {
        await Factory.SeedUserAsync("native-reuse");
        var client = NewClient();
        var login = await BodyLoginAsync(client, "native-reuse");

        // First refresh rotates the token away.
        var first = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new { RefreshToken = login.RefreshToken });
        first.EnsureSuccessStatusCode();
        var rotated = (await first.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!;

        // Replaying the ORIGINAL token = reuse -> chain revocation.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The rotated token (the whole chain) is dead too.
        var chained = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new { RefreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, chained.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithNoCookieAndNoBody_Is401()
    {
        var response = await NewClient().PostAsync("/api/v1/auth/refresh-token", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithMalformedBody_Is401_NotServerError()
    {
        var response = await NewClient().PostAsync("/api/v1/auth/refresh-token",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithBodyToken_RevokesIt()
    {
        await Factory.SeedUserAsync("native-logout");
        var client = NewClient();
        var login = await BodyLoginAsync(client, "native-logout");

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new { RefreshToken = login.RefreshToken });
        logout.EnsureSuccessStatusCode();

        // The revoked token can no longer refresh.
        var after = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
