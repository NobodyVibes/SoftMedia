using System.Net;
using System.Net.Http.Headers;
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
