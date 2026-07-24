using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// B-01 (serve-time layer) — /stream/{id} must refuse a bitrate-capped user's VIDEO
/// when the source exceeds the cap (the plan endpoint already refuses direct play,
/// but the raw endpoint was reachable regardless of any plan). Music is exempt by
/// design: the cap is a video-streaming control.
public class StreamBitrateCapIntegrationTests : IntegrationTestBase
{
    private HttpClient ClientFor(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private async Task<(Guid movieId, Guid trackId, string dir)> SeedAsync(long movieBitrateBps)
    {
        // Real files inside the library root: the endpoint's LFI jail and
        // File.Exists checks run before the cap gate.
        var dir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var moviePath = Path.Combine(dir, "movie.mp4");
        var trackPath = Path.Combine(dir, "track.flac");
        await File.WriteAllBytesAsync(moviePath, new byte[] { 0, 1, 2, 3 });
        await File.WriteAllBytesAsync(trackPath, new byte[] { 0, 1, 2, 3 });

        return await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Cap-{Guid.NewGuid():N}"[..10], Type = LibraryType.Movie, Paths = new() { dir } };
            db.Libraries.Add(lib);
            var movie = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Big Movie",
                SortTitle = "Big Movie",
                Path = moviePath,
                Type = MediaType.Movie,
                Bitrate = movieBitrateBps,
            };
            var track = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Loud Track",
                SortTitle = "Loud Track",
                Path = trackPath,
                Type = MediaType.Audio,
                Bitrate = movieBitrateBps, // same figure — must NOT be capped for audio
            };
            db.MediaItems.AddRange(movie, track);
            await db.SaveChangesAsync();
            return (movie.Id, track.Id, dir);
        });
    }

    [Fact]
    public async Task CappedUser_VideoAboveCap_Is403_MusicAndUncappedUnaffected()
    {
        var (movieId, trackId, _) = await SeedAsync(movieBitrateBps: 20_000_000); // 20 Mbps
        var capped = await Factory.SeedUserAsync("cap-user");
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(capped.Id))!.MaxStreamBitrateKbps = 3000;
            await db.SaveChangesAsync();
        });
        var uncapped = await Factory.SeedUserAsync("cap-free");

        var blocked = await ClientFor(capped).GetAsync($"/api/v1/stream/{movieId}");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        var music = await ClientFor(capped).GetAsync($"/api/v1/stream/{trackId}");
        Assert.True(music.IsSuccessStatusCode, $"music should stream, got {music.StatusCode}");

        var free = await ClientFor(uncapped).GetAsync($"/api/v1/stream/{movieId}");
        Assert.True(free.IsSuccessStatusCode, $"uncapped user should stream, got {free.StatusCode}");
    }

    [Fact]
    public async Task StreamPlanUrl_EmbedsAServerMintedMediaToken_ThatActuallyAuthenticates()
    {
        // WS-6 regression (found live): the plan URL echoed the CALLER'S bearer token —
        // which is now the full ACCESS token (the plan POST authenticates via header),
        // and T6.1 rejects access tokens in query strings — so every DirectPlay src
        // fetch 401'd. The server must mint a reduced media token for the URL instead,
        // mirroring what the cast branch already did.
        var (movieId, _, _) = await SeedAsync(movieBitrateBps: 2_000_000);
        var user = await Factory.SeedUserAsync("plan-url-user");
        var client = ClientFor(user); // Authorization: Bearer <ACCESS token>

        var resp = await client.PostAsJsonAsync($"/api/transcode/{movieId}/plan", new
        {
            videoCodecs = new[] { "h264" },
            audioCodecs = new[] { "aac" },
            maxAudioChannels = 2,
            supportsHdr = false,
            supportedContainers = new[] { "hls", "mp4" },
            supportedSubtitleFormats = new[] { "vtt" },
        });
        resp.EnsureSuccessStatusCode();
        var plan = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var url = plan.GetProperty("url").GetString()!;
        var embedded = url.Split("token=")[1].Split('&')[0];

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(embedded);
        Assert.Equal(CastTokenClaims.MediaUse, jwt.Claims.First(c => c.Type == CastTokenClaims.TokenUse).Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.Role || c.Type == "role");

        // The minted token must authenticate a bare query-string request — exactly how
        // a <video src> uses it (no Authorization header available).
        var anon = Factory.CreateClient();
        var stream = await anon.GetAsync($"/api/v1/stream/{movieId}?token={embedded}");
        Assert.True(stream.IsSuccessStatusCode, $"query-token stream should work, got {stream.StatusCode}");
    }

    /// SR-WI-028: seeds one video item with explicit Bitrate/Size/Duration/Type so the
    /// estimate path (null bitrate) and non-Movie video types can be exercised.
    private async Task<Guid> SeedVideoAsync(long? bitrateBps, long sizeBytes, double durationSeconds,
        MediaType type = MediaType.Movie, string fileName = "video.mp4")
    {
        var dir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(path, new byte[] { 0, 1, 2, 3 });

        return await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Cap-{Guid.NewGuid():N}"[..10], Type = LibraryType.Movie, Paths = new() { dir } };
            db.Libraries.Add(lib);
            var item = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Cap Video",
                SortTitle = "Cap Video",
                Path = path,
                Type = type,
                Bitrate = bitrateBps,
                Size = sizeBytes,
                Duration = durationSeconds,
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
    }

    private async Task<User> SeedCappedUserAsync(string name, int capKbps)
    {
        var user = await Factory.SeedUserAsync(name);
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(user.Id))!.MaxStreamBitrateKbps = capKbps;
            await db.SaveChangesAsync();
        });
        return user;
    }

    // SR-WI-028: unprobed/legacy rows (Bitrate=null) used to bypass the cap gate
    // entirely. When Size and Duration are known, the gate now estimates the average
    // bitrate as Size*8/DurationSeconds.
    [Fact]
    public async Task CappedUser_UnprobedVideo_GatedBySizeDurationEstimate()
    {
        // 9 GB over 3600 s => 20 Mbps estimate, above the 3000 kbps cap.
        var overId = await SeedVideoAsync(bitrateBps: null, sizeBytes: 9_000_000_000, durationSeconds: 3600);
        // 900 MB over 3600 s => 2 Mbps estimate, below the cap.
        var underId = await SeedVideoAsync(bitrateBps: null, sizeBytes: 900_000_000, durationSeconds: 3600);
        var capped = await SeedCappedUserAsync("cap-estimate", 3000);

        var blocked = await ClientFor(capped).GetAsync($"/api/v1/stream/{overId}");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        var ok = await ClientFor(capped).GetAsync($"/api/v1/stream/{underId}");
        Assert.True(ok.IsSuccessStatusCode, $"estimate under cap should stream, got {ok.StatusCode}");
    }

    // SR-WI-028: when NEITHER a probed bitrate nor size+duration exist, the historical
    // allow-through stands (we won't block playback on missing metadata).
    [Fact]
    public async Task CappedUser_UnprobedVideo_NoSizeOrDuration_StillStreams()
    {
        var id = await SeedVideoAsync(bitrateBps: null, sizeBytes: 0, durationSeconds: 0);
        var capped = await SeedCappedUserAsync("cap-unknown", 3000);

        var resp = await ClientFor(capped).GetAsync($"/api/v1/stream/{id}");
        Assert.True(resp.IsSuccessStatusCode, $"unknown-bitrate video should stream, got {resp.StatusCode}");
    }

    // SR-WI-028: the gate covers every video item served here, not just Movie/Episode —
    // e.g. a video clip in a photo library (Type=Photo, video/* mime).
    [Fact]
    public async Task CappedUser_NonMovieVideoType_IsGated()
    {
        var id = await SeedVideoAsync(bitrateBps: 20_000_000, sizeBytes: 0, durationSeconds: 0,
            type: MediaType.Photo, fileName: "clip.mp4");
        var capped = await SeedCappedUserAsync("cap-photo-clip", 3000);

        var blocked = await ClientFor(capped).GetAsync($"/api/v1/stream/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    // SR-WI-028 guard: users with NO cap must see zero behavior change on unprobed rows.
    [Fact]
    public async Task UncappedUser_UnprobedVideo_Streams()
    {
        var id = await SeedVideoAsync(bitrateBps: null, sizeBytes: 9_000_000_000, durationSeconds: 3600);
        var user = await Factory.SeedUserAsync("no-cap-unprobed");

        var resp = await ClientFor(user).GetAsync($"/api/v1/stream/{id}");
        Assert.True(resp.IsSuccessStatusCode, $"uncapped user should stream, got {resp.StatusCode}");
    }

    [Fact]
    public async Task CappedUser_VideoWithinCap_Streams()
    {
        var (movieId, _, _) = await SeedAsync(movieBitrateBps: 2_000_000); // 2 Mbps < 3000 kbps
        var capped = await Factory.SeedUserAsync("cap-user2");
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(capped.Id))!.MaxStreamBitrateKbps = 3000;
            await db.SaveChangesAsync();
        });

        var ok = await ClientFor(capped).GetAsync($"/api/v1/stream/{movieId}");
        Assert.True(ok.IsSuccessStatusCode, $"within-cap video should stream, got {ok.StatusCode}");
    }
}
