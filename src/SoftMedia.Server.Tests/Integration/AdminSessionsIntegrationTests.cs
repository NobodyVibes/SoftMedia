using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Sessions;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// R-WI-016 — admin Now-Playing endpoint. Admin-gating, both session kinds listed
/// with resolved titles/usernames, terminate kills only live transcode sessions
/// (freeing the count-derived cap slot), direct plays are read-only.
public class AdminSessionsIntegrationTests : IntegrationTestBase
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

    private async Task<(User admin, User viewer, Guid mediaId)> SeedAsync()
    {
        var admin = await Factory.SeedUserAsync("sess-admin", role: UserRole.Admin);
        var viewer = await Factory.SeedUserAsync("sess-viewer");
        var mediaId = await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "Sess-Test", Type = LibraryType.Movie, Paths = new() { "/m" } };
            db.Libraries.Add(lib);
            var item = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Session Movie",
                SortTitle = "Session Movie",
                Path = "/m/session.mkv",
                Type = MediaType.Movie,
                Duration = 5400,
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
        return (admin, viewer, mediaId);
    }

    /// Registers a fabricated (process-less) transcode session — StopSession
    /// null-guards Process and the missing directory, so terminate works on it.
    private TranscodeSessionKey AddTranscodeSession(
        Guid mediaId,
        Guid userId,
        string? sid = "sid-test-1",
        TranscodeState state = TranscodeState.Transcoding,
        DateTime? lastClientRequest = null)
    {
        var manager = Factory.Services.GetRequiredService<ITranscodeSessionManager>();
        var key = new TranscodeSessionKey(mediaId, userId, null, sid);
        var added = manager.TryAddSession(new TranscodeSession
        {
            Key = key,
            UserId = userId,
            InputPath = "/m/session.mkv",
            SessionDirectory = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N")),
            TargetResolution = "720p",
            TargetCodec = "h264",
            MaxBitrate = 3000,
            SeekPosition = 120,
            ClientSegmentIndex = 10, // 120 + 10*6 = 180s playhead estimate
            State = state,
            LastClientRequestTime = lastClientRequest ?? DateTime.UtcNow,
        });
        Assert.True(added);
        return key;
    }

    [Fact]
    public async Task Sessions_Anonymous_Is401_AndNonAdmin_Is403()
    {
        var (_, viewer, _) = await SeedAsync();

        var anon = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/sessions")).StatusCode);

        var userClient = ClientFor(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/v1/admin/sessions")).StatusCode);
    }

    [Fact]
    public async Task Sessions_ListsTranscode_WithResolvedNamesAndPlayhead()
    {
        var (admin, viewer, mediaId) = await SeedAsync();
        AddTranscodeSession(mediaId, viewer.Id);

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");

        var row = Assert.Single(rows!);
        Assert.Equal("Transcode", row.Type);
        Assert.Equal("sess-viewer", row.UserName);
        Assert.Equal("Session Movie", row.MediaTitle);
        Assert.Equal(180, row.PositionSeconds);   // SeekPosition 120 + 10 segments × 6s
        Assert.Equal(5400, row.DurationSeconds);
        Assert.Equal("720p", row.Resolution);
        Assert.True(row.CanTerminate);
        Assert.Equal("sid-test-1", row.StreamId);
    }

    [Fact]
    public async Task Sessions_ListsDirectPlay_ReadOnly_WithBeatPosition()
    {
        var (admin, viewer, mediaId) = await SeedAsync();
        var registry = Factory.Services.GetRequiredService<IActiveStreamRegistry>();
        registry.OnResponseStarted(viewer.Id, mediaId);

        // The beat heartbeat flows through the real endpoint (also proves the
        // InteractionController wiring), carrying the playhead.
        var beat = await ClientFor(viewer).PostAsJsonAsync(
            $"/api/v1/interaction/{mediaId}/progress", new { position = 321.5 });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");

        var row = Assert.Single(rows!);
        Assert.Equal("DirectPlay", row.Type);
        Assert.Equal("sess-viewer", row.UserName);
        Assert.Equal("Session Movie", row.MediaTitle);
        Assert.Equal(321.5, row.PositionSeconds);
        Assert.False(row.CanTerminate);
    }

    [Fact]
    public async Task CompletedSession_StillServingSegments_IsListed_StaleOneIsNot()
    {
        // ffmpeg finishing ≠ the viewer finishing: short files are fully encoded
        // while the client is still streaming (found live: a fully-encoded clip
        // vanished mid-playback). Recent client requests keep it listed as Serving;
        // no requests for over a minute means the play really ended.
        var (admin, viewer, mediaId) = await SeedAsync();
        AddTranscodeSession(mediaId, viewer.Id, sid: "sid-serving",
            state: TranscodeState.Completed, lastClientRequest: DateTime.UtcNow);
        AddTranscodeSession(mediaId, viewer.Id, sid: "sid-stale",
            state: TranscodeState.Completed, lastClientRequest: DateTime.UtcNow.AddMinutes(-5));

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");

        var row = Assert.Single(rows!);
        Assert.Equal("Serving", row.State);
        Assert.Equal("sid-serving", row.StreamId);
    }

    [Fact]
    public async Task DirectPlay_WithoutABeat_ShowsAsStreaming_NotPlaying()
    {
        // The music player's gapless preload opens /stream without playing.
        var (admin, viewer, mediaId) = await SeedAsync();
        var registry = Factory.Services.GetRequiredService<IActiveStreamRegistry>();
        registry.OnResponseStarted(viewer.Id, mediaId);

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");

        Assert.Equal("Streaming", Assert.Single(rows!).State);
    }

    [Fact]
    public async Task Beats_DuringATranscode_CreateNoPhantomDirectPlay()
    {
        // A transcode viewer's beats hit the same endpoint — they must not
        // fabricate a DirectPlay row next to the Transcode row.
        var (admin, viewer, mediaId) = await SeedAsync();
        AddTranscodeSession(mediaId, viewer.Id);

        var beat = await ClientFor(viewer).PostAsJsonAsync(
            $"/api/v1/interaction/{mediaId}/progress", new { position = 200.0 });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        Assert.Single(rows!);
        Assert.Equal("Transcode", rows![0].Type);
    }

    [Fact]
    public async Task Beat_WithNoTranscode_CreatesTheDirectPlayRow()
    {
        // Recovery paths: a fully browser-cached play never opens /stream, and a
        // server restart wipes the in-memory registry mid-play (found live). The
        // beat alone must surface the play.
        var (admin, viewer, mediaId) = await SeedAsync();

        var beat = await ClientFor(viewer).PostAsJsonAsync(
            $"/api/v1/interaction/{mediaId}/progress", new { position = 123.0 });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        var row = Assert.Single(rows!);
        Assert.Equal("DirectPlay", row.Type);
        Assert.Equal("Playing", row.State);
        Assert.Equal(123, row.PositionSeconds);
    }

    [Fact]
    public async Task DormantSession_DoesNotSuppressBeatCreation_AndStaysHiddenWhenStale()
    {
        // Review HIGH ×2: closing the player parks the transcode DORMANT for up to
        // 24h. (a) An unfiltered guard suppressed direct-play tracking of that media
        // all day; (b) listing Dormant unconditionally showed a phantom "Paused" row
        // all day. A stale dormant session must be invisible AND must not block the
        // user's later direct play of the same media.
        var (admin, viewer, mediaId) = await SeedAsync();
        AddTranscodeSession(mediaId, viewer.Id, sid: "sid-dormant",
            state: TranscodeState.Dormant, lastClientRequest: DateTime.UtcNow.AddHours(-3));

        var beat = await ClientFor(viewer).PostAsJsonAsync(
            $"/api/v1/interaction/{mediaId}/progress", new { position = 50.0 });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        var row = Assert.Single(rows!); // no phantom "Paused" row
        Assert.Equal("DirectPlay", row.Type);
        Assert.Equal(50, row.PositionSeconds);
    }

    [Fact]
    public async Task Beats_AboveTheRatingCeiling_CreateNoRow()
    {
        // Review MED: beats accept arbitrary ids — creation must respect the
        // caller's content gates, or a user can paint the dashboard with media
        // they cannot access.
        var (admin, _, _) = await SeedAsync();
        var restricted = await Factory.SeedUserAsync("sess-restricted");
        var rRatedId = await Factory.WithDbAsync(async db =>
        {
            var user = await db.Users.FindAsync(restricted.Id);
            user!.MaxRating = "G";
            var lib = new Library { Id = Guid.NewGuid(), Name = "Sess-Rated", Type = LibraryType.Movie, Paths = new() { "/r" } };
            db.Libraries.Add(lib);
            var item = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "R Rated",
                SortTitle = "R Rated",
                Path = "/r/movie.mkv",
                Type = MediaType.Movie,
                ContentRating = "R",
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
        restricted.MaxRating = "G"; // keep the token's claim in sync with the DB row

        var beat = await ClientFor(restricted).PostAsJsonAsync(
            $"/api/v1/interaction/{rRatedId}/progress", new { position = 60.0 });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        Assert.Empty(rows!);
    }

    [Fact]
    public async Task BookBeats_AreNotPlayback_AndCreateNoRow()
    {
        var (admin, viewer, _) = await SeedAsync();
        var bookId = await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "Sess-Books", Type = LibraryType.Book, Paths = new() { "/b" } };
            db.Libraries.Add(lib);
            var book = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "A Book",
                SortTitle = "A Book",
                Path = "/b/book.epub",
                Type = MediaType.Book,
            };
            db.MediaItems.Add(book);
            await db.SaveChangesAsync();
            return book.Id;
        });

        var beat = await ClientFor(viewer).PostAsJsonAsync(
            $"/api/v1/interaction/{bookId}/progress", new { position = 0.0, bookLocation = "ch3" });
        beat.EnsureSuccessStatusCode();

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        Assert.Empty(rows!);
    }

    [Fact]
    public async Task Terminate_RemovesTheSession_FreeingItsCapSlot()
    {
        var (admin, viewer, mediaId) = await SeedAsync();
        var key = AddTranscodeSession(mediaId, viewer.Id);

        var response = await ClientFor(admin).DeleteAsync(
            $"/api/v1/admin/sessions?mediaId={key.MediaId}&userId={key.UserId}&sid={key.StreamId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The cap is COUNTED from live sessions — removal IS the slot release.
        var manager = Factory.Services.GetRequiredService<ITranscodeSessionManager>();
        Assert.Null(manager.GetSession(key));

        var rows = await ClientFor(admin).GetFromJsonAsync<List<SessionRow>>("/api/v1/admin/sessions");
        Assert.Empty(rows!);
    }

    [Fact]
    public async Task Terminate_UnknownKey_Is404_AndNonAdmin_Is403()
    {
        var (admin, viewer, mediaId) = await SeedAsync();
        var key = AddTranscodeSession(mediaId, viewer.Id);

        var missing = await ClientFor(admin).DeleteAsync(
            $"/api/v1/admin/sessions?mediaId={Guid.NewGuid()}&userId={viewer.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var forbidden = await ClientFor(viewer).DeleteAsync(
            $"/api/v1/admin/sessions?mediaId={key.MediaId}&userId={key.UserId}&sid={key.StreamId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // The session survived both bad requests.
        var manager = Factory.Services.GetRequiredService<ITranscodeSessionManager>();
        Assert.NotNull(manager.GetSession(key));
    }

    private sealed record SessionRow(
        string Type,
        string State,
        Guid UserId,
        string UserName,
        Guid MediaId,
        string MediaTitle,
        double PositionSeconds,
        double DurationSeconds,
        DateTime StartedAt,
        string? Resolution,
        string? Codec,
        int? MaxBitrateKbps,
        bool CanTerminate,
        int? SubtitleTrackIndex,
        string? StreamId);
}
