using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// <summary>
/// The startup runway: how much playlist the FIRST master.m3u8 response must carry.
/// ffmpeg publishes master.m3u8 the moment segment 0 closes, and a player only discovers
/// later segments when it RELOADS the playlist (hls.js: up to ~2x target duration after the
/// initial load). Serving that 1-segment playlist therefore stalls playback a few seconds in
/// even when ffmpeg already has minutes of output on disk — the live-QA 2026-07-25 failure,
/// where a 25x-realtime NVENC session held 168s of segments while the player starved on 6s.
/// </summary>
public class StartupRunwayTests
{
    private static HlsPlaylistInfo Playlist(int segments, bool endList = false) =>
        new(segments, segments * 6.0, endList);

    [Fact]
    public void SingleSegmentPlaylist_IsNotReady()
    {
        // The regression itself: 6s of runway against a ~12s reload cycle.
        Assert.False(TranscodeService.StartupRunwayReady(Playlist(1), graceExpired: false));
    }

    [Fact]
    public void RunwayLengthPlaylist_IsReady()
    {
        Assert.True(TranscodeService.StartupRunwayReady(
            Playlist(TranscodeService.StartupRunwaySegments), graceExpired: false));
    }

    [Fact]
    public void RunwayOutlastsAnHlsJsPlaylistReloadCycle()
    {
        // hls.js waits up to 2x EXT-X-TARGETDURATION before its first reload; the runway has
        // to cover that gap or the player drains before it can learn about more segments.
        var runwaySeconds = TranscodeService.StartupRunwaySegments * TranscodeService.HlsSegmentDurationSeconds;
        Assert.True(runwaySeconds > 2 * TranscodeService.HlsSegmentDurationSeconds);
    }

    [Fact]
    public void GraceExpired_ServesWhateverExists()
    {
        // A slow encoder must not have its time-to-first-frame held hostage to segments it
        // cannot produce yet — after the grace window it serves the short playlist.
        Assert.True(TranscodeService.StartupRunwayReady(Playlist(1), graceExpired: true));
    }

    [Fact]
    public void FullyTranscodedStream_IsReadyRegardlessOfLength()
    {
        // ENDLIST means there will never be more segments; waiting for a runway that can
        // never arrive would just burn the grace window.
        Assert.True(TranscodeService.StartupRunwayReady(Playlist(1, endList: true), graceExpired: false));
    }

    [Fact]
    public void UnreadablePlaylist_IsNotReady_UntilGraceExpires()
    {
        // null = the playlist was caught mid-rewrite. That is "no runway yet", not "ready" —
        // treating it as ready would re-serve the 1-segment playlist on a lost race.
        Assert.False(TranscodeService.StartupRunwayReady(null, graceExpired: false));
        Assert.True(TranscodeService.StartupRunwayReady(null, graceExpired: true));
    }
}
