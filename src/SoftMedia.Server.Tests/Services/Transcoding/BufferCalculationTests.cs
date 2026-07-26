using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// <summary>
/// Buffer measurement for the throttle monitor. Transcode segments really are ~6s each
/// (keyframes are forced on that cadence), but a REMUX (-c copy) can only cut on SOURCE
/// keyframes — its EXTINF durations drift far from hls_time, so buffer seconds must come
/// from the playlist's actual durations, not index arithmetic × 6.
/// </summary>
public class BufferCalculationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"buffer-{Guid.NewGuid():N}");

    public BufferCalculationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WritePlaylist(string content) =>
        File.WriteAllText(Path.Combine(_dir, "master.m3u8"), content);

    private HlsService CreateHls() => new(NullLogger<HlsService>.Instance);

    // ---- GetActualPlaylistDuration: the primitive CalculateBufferSeconds diffs ---------

    [Fact]
    public void CumulativeDuration_SumsOnlyTheFirstNSegments()
    {
        // Remux-shaped playlist: durations follow the source GOP, not hls_time.
        WritePlaylist("#EXTM3U\n#EXTINF:2.002,\nseg_000.m4s\n#EXTINF:10.677,\nseg_001.m4s\n#EXTINF:4.171,\nseg_002.m4s\n");

        var hls = CreateHls();
        Assert.Equal(2.002, hls.GetActualPlaylistDuration(_dir, 1), 3);
        Assert.Equal(12.679, hls.GetActualPlaylistDuration(_dir, 2), 3);
        Assert.Equal(16.850, hls.GetActualPlaylistDuration(_dir, 3), 3);
    }

    [Fact]
    public void CumulativeDuration_CountBeyondPlaylist_ReturnsListedTotal()
    {
        // LatestSegmentIndex comes from DISK, which can lead the playlist by a segment —
        // asking past the end must return what is listed, not throw or over-count.
        WritePlaylist("#EXTM3U\n#EXTINF:6.006,\nseg_000.m4s\n#EXTINF:6.006,\nseg_001.m4s\n");

        Assert.Equal(12.012, CreateHls().GetActualPlaylistDuration(_dir, 99), 3);
    }

    [Fact]
    public void CumulativeDuration_MissingPlaylist_FallsBackToSixSecondEstimate()
    {
        Assert.Equal(5 * 6.0, CreateHls().GetActualPlaylistDuration(_dir, 5), 3);
    }

    // ---- The remux scenario the fix exists for ----------------------------------------

    [Fact]
    public void ShortGopRemux_RealBufferIsAThirdOfTheOldEstimate()
    {
        // 20 segments of 2s (source keyframes every 2s). The old (count × 6) arithmetic
        // called this 120s and SUSPENDED ffmpeg while the viewer really had 40s left.
        var extinf = string.Concat(Enumerable.Repeat("#EXTINF:2.000,\nseg.m4s\n", 20));
        WritePlaylist("#EXTM3U\n" + extinf);

        var actual = CreateHls().GetActualPlaylistDuration(_dir, 20);

        Assert.Equal(40.0, actual, 3);
        Assert.True(actual < TranscodeService.ThrottleBufferMaxSeconds,
            "a short-GOP remux buffer the old estimate throttled at must measure below the threshold");
    }

    // ---- The fallback rule -------------------------------------------------------------

    [Fact]
    public void Fallback_PositivePlaylistValue_Wins()
    {
        Assert.Equal(40, TranscodeService.BufferSecondsWithFallback(40.2, bufferSegments: 20));
    }

    [Fact]
    public void Fallback_PlaylistLaggingDisk_EstimatesFromSegmentCount()
    {
        // A zero/negative playlist-derived value (playlist mid-rewrite, or lagging the
        // segments on disk) must NOT report an empty buffer — that would spuriously
        // resume a correctly-throttled encoder every monitor tick.
        Assert.Equal(3 * TranscodeService.HlsSegmentDurationSeconds,
            TranscodeService.BufferSecondsWithFallback(0, bufferSegments: 3));
    }
}
