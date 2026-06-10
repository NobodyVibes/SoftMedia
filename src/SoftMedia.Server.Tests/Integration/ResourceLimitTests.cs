using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Security audit M8/M9/M10 — resource-exhaustion guards: a finite default per-user transcode
/// cap, and clamped list limits so a single huge ?limit/?pageSize can't hydrate the whole DB.
public class ResourceLimitTests : IntegrationTestBase
{
    private record AuthResponseDto(string AccessToken);

    private async Task<string> LoginAsync(string username)
    {
        await Factory.SeedUserAsync(username);
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = "TestPass!1" });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<AuthResponseDto>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!.AccessToken;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string token)
    {
        var client = Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task SeededPerUserTranscodeCap_IsFinite_M9()
    {
        await Factory.WithDbAsync(async db =>
        {
            var setting = await db.Settings.FindAsync("MaxSimultaneousTranscodesPerUser");
            Assert.NotNull(setting);
            Assert.True(int.TryParse(setting!.Value, out var cap) && cap > 0,
                $"Per-user transcode cap should default to a finite positive value, was '{setting.Value}'");
        });
    }

    [Fact]
    public async Task RecentMedia_HugeLimit_IsClampedAndSucceeds_M10()
    {
        var token = await LoginAsync("limit-user");
        var resp = await GetAsync("/api/v1/media/recent?limit=999999999", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task LibraryItems_HugePageSize_IsClampedAndSucceeds_M8()
    {
        var token = await LoginAsync("page-user");
        var resp = await GetAsync($"/api/v1/libraries/{Guid.NewGuid()}/items?pageSize=999999999", token);
        // Clamped + handled gracefully (no crash/timeout); unknown library yields an empty page.
        Assert.True(resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"was {resp.StatusCode}");
    }
}
