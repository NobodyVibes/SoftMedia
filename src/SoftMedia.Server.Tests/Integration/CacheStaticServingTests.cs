using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// MC-WI-001 + AA-WI-001..003 — static cache serving rules:
/// - /cache/subtitles and /cache/trickplay are NEVER served statically (404, anti-probe;
///   their only doors are the authorized transcode/trickplay endpoints);
/// - /cache/images requires an authenticated caller: the WS-6 media token in the query
///   string (what &lt;img&gt; tags use), or a header-authed full session. Cast tokens stay
///   hard-scoped to their stream routes (401 here), and full access tokens are never
///   accepted from a query string. 401 (not 404) so the client can tell "refresh the
///   token" apart from "missing file".
public class CacheStaticServingTests : IClassFixture<SoftMediaWebApplicationFactory>
{
    private readonly SoftMediaWebApplicationFactory _factory;

    public CacheStaticServingTests(SoftMediaWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/cache/subtitles/0011223344556677_s0_638691362920716673.vtt")]
    [InlineData("/cache/trickplay/00112233445566778899aabbccddeeff/manifest.json")]
    [InlineData("/cache/trickplay/00112233445566778899aabbccddeeff/sheet-0.jpg")]
    public async Task SubtitlesAndTrickplay_AreNeverServedStatically(string path)
    {
        // The content root is the real server project, so REAL cached files may exist
        // at these prefixes on a dev machine — the middleware must 404 regardless of
        // file existence, which is exactly what makes the paths unprobeable. A valid
        // media token must not change the answer either: these are full denials.
        var (user, _) = await SeedUserWithTokensAsync();
        var mediaToken = MintMediaToken(user);
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}?token={mediaToken}")).StatusCode);
    }

    [Fact]
    public async Task CacheImages_Anonymous_Is401()
    {
        using var image = await TempImageAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync(image.WebPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_MediaTokenInQuery_Is200()
    {
        using var image = await TempImageAsync();
        var (user, _) = await SeedUserWithTokensAsync();
        var mediaToken = MintMediaToken(user);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"{image.WebPath}?token={mediaToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_CastTokenInQuery_Is401_ScopeStaysLocked()
    {
        // Decision #2 (adversarial review): the cast token remains hard-scoped to ONE
        // media item's stream routes — the cast poster carries the MEDIA token instead.
        using var image = await TempImageAsync();
        var (user, castToken) = await SeedUserWithTokensAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"{image.WebPath}?token={castToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_FullAccessTokenInQuery_Is401()
    {
        // WS-6 T6.1: a full role-bearing access token must never be accepted from a
        // query string — logs, proxies and history would capture it.
        using var image = await TempImageAsync();
        var (user, _) = await SeedUserWithTokensAsync();
        var accessToken = MintAccessToken(user);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"{image.WebPath}?access_token={accessToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_FullAccessTokenInHeader_Is200()
    {
        using var image = await TempImageAsync();
        var (user, _) = await SeedUserWithTokensAsync();
        var accessToken = MintAccessToken(user);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(image.WebPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_GarbageToken_Is401()
    {
        using var image = await TempImageAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"{image.WebPath}?token=not-a-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CacheImages_BannedUsersMediaToken_Is401_ViaEagerInvalidation()
    {
        // AA-WI-011: the eligibility verdict is cached, so this test proves the eager
        // invalidation path — warm the cache with a successful request, ban the user
        // (invalidating), and the SAME still-unexpired token must stop working.
        using var image = await TempImageAsync();
        var (user, _) = await SeedUserWithTokensAsync();
        var mediaToken = MintMediaToken(user);
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"{image.WebPath}?token={mediaToken}")).StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            var row = await db.Users.FirstAsync(u => u.Id == user.Id);
            row.IsBanned = true;
            await db.SaveChangesAsync();
        });
        _factory.Services.GetRequiredService<IUserEligibilityCache>().Invalidate(user.Id);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"{image.WebPath}?token={mediaToken}")).StatusCode);
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<(User User, string CastToken)> SeedUserWithTokensAsync()
    {
        var user = await _factory.SeedUserAsync($"img-{Guid.NewGuid():N}");
        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var castToken = tokens.GenerateCastToken(user, Guid.NewGuid());
        return (user, castToken);
    }

    private string MintMediaToken(User user)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateMediaToken(user).Token;
    }

    private string MintAccessToken(User user)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateAccessToken(user);
    }

    /// A unique throwaway poster in the REAL wwwroot/cache/images (the factory host's
    /// content root is the server project — see the content-root shared-state note in
    /// the factory). Deleted on dispose, even when an assertion fails.
    private static async Task<TempImage> TempImageAsync()
    {
        var imagesDir = Path.Combine(FindWebRootUpwards(), "cache", "images", "movies");
        Directory.CreateDirectory(imagesDir);
        var name = $"it-{Guid.NewGuid():N}_poster.jpg";
        var filePath = Path.Combine(imagesDir, name);
        await File.WriteAllBytesAsync(filePath, new byte[] { 0xFF, 0xD8, 0xFF });
        return new TempImage(filePath, $"/cache/images/movies/{name}");
    }

    private sealed record TempImage(string FilePath, string WebPath) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(FilePath); } catch { }
        }
    }

    /// The factory host's content root is the SoftMedia.Server project directory;
    /// resolve its wwwroot relative to the test assembly location.
    private static string FindWebRootUpwards()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "SoftMedia.Server", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "src", "SoftMedia.Server", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate SoftMedia.Server/wwwroot above the test assembly.");
    }
}
