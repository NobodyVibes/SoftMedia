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

/// NR-WI-010/011/014 — the Session 4 HTTP surfaces: anonymous branding, admin-only
/// connection info + logs, and the extras list/stream path incl. the library jail.
public class SystemAndExtrasIntegrationTests : IntegrationTestBase, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly string _libDir;

    public SystemAndExtrasIntegrationTests()
    {
        _libDir = Path.Combine(Path.GetTempPath(), "softmedia-extras-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_libDir, recursive: true); } catch { /* best-effort */ }
    }

    private HttpClient BearerClient(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateAccessToken(user));
        return client;
    }

    // ---- NR-WI-010: branding + connection info ----

    [Fact]
    public async Task Branding_IsAnonymous_AndReturnsDefaultName()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/system/branding");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("SoftMedia", body.GetProperty("serverName").GetString());
    }

    [Fact]
    public async Task ConnectionInfoAndLogs_AreAdminOnly()
    {
        var user = await Factory.SeedUserAsync("sysinfo-user");
        var admin = await Factory.SeedUserAsync("sysinfo-admin", role: UserRole.Admin);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Factory.CreateClient().GetAsync("/api/v1/system/connection-info")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await BearerClient(user).GetAsync("/api/v1/system/connection-info")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await BearerClient(user).GetAsync("/api/v1/system/logs")).StatusCode);

        var info = await BearerClient(admin).GetAsync("/api/v1/system/connection-info");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);

        var logs = await BearerClient(admin).GetAsync("/api/v1/system/logs?take=50");
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        var logsBody = await logs.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.True(logsBody.TryGetProperty("currentLevel", out _));
    }

    // ---- NR-WI-014: extras list + stream ----

    private async Task<(User user, Guid movieId)> SeedMovieWithTrailerAsync()
    {
        var user = await Factory.SeedUserAsync($"extras-{Guid.NewGuid():N}"[..20]);
        File.WriteAllBytes(Path.Combine(_libDir, "Film.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_libDir, "Film-trailer.mkv"), new byte[] { 9, 9, 9 });

        var movieId = Guid.NewGuid();
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "Extras Lib", Type = LibraryType.Movie, Paths = new() { _libDir } };
            db.Libraries.Add(lib);
            db.MediaItems.Add(new MediaItem
            {
                Id = movieId,
                LibraryId = lib.Id,
                Title = "Film",
                SortTitle = "Film",
                Type = MediaType.Movie,
                Path = Path.Combine(_libDir, "Film.mkv"),
            });
            await db.SaveChangesAsync();
        });
        return (user, movieId);
    }

    [Fact]
    public async Task Extras_ListAndStream_WorkForAuthorizedUser()
    {
        var (user, movieId) = await SeedMovieWithTrailerAsync();
        var client = BearerClient(user);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/stream/{movieId}/extras", JsonOpts);
        Assert.Equal(1, list.GetArrayLength());
        var index = list[0].GetProperty("index").GetInt32();

        var stream = await client.GetAsync($"/api/v1/stream/{movieId}/extras/{index}");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.True(stream.Content.Headers.ContentLength > 0);

        // Range support — native players seek.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/stream/{movieId}/extras/{index}");
        req.Headers.Range = new RangeHeaderValue(0, 1);
        var ranged = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
    }

    [Fact]
    public async Task Extras_UnknownItemOrIndex_Is404()
    {
        var (user, movieId) = await SeedMovieWithTrailerAsync();
        var client = BearerClient(user);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/stream/{Guid.NewGuid()}/extras")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/stream/{movieId}/extras/42")).StatusCode);
    }

    [Fact]
    public async Task Extras_RequireAuthentication()
    {
        var (_, movieId) = await SeedMovieWithTrailerAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Factory.CreateClient().GetAsync($"/api/v1/stream/{movieId}/extras")).StatusCode);
    }
}
