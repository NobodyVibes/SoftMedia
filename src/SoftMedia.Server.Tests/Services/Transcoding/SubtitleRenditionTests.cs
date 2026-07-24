using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// B-13/B-14/B-17 — the HLS subtitle rendition and the seek-offset VTT rewrite.
public class SubtitleRenditionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));

    public SubtitleRenditionTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- B-13: master rewrite must reference a PLAYLIST, not the raw .vtt, and
    //      must not auto-select (the web client renders its own track) ----

    [Fact]
    public async Task MasterRewrite_PointsRenditionAtThePlaylist_NotTheRawVtt_AndDoesNotAutoSelect()
    {
        var vttPath = Path.Combine(_dir, "subtitles.vtt");
        await File.WriteAllTextAsync(vttPath, "WEBVTT\n");
        var svc = new HlsManifestService(NullLogger<HlsManifestService>.Instance);
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nindex.m3u8\n";

        var rewritten = Encoding.UTF8.GetString(await svc.GenerateMasterPlaylistAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(master)), "tok", Guid.NewGuid().ToString(), 2, vttPath, "sid1"));

        Assert.Contains("subtitles.m3u8?", rewritten);
        Assert.DoesNotContain("subtitles.vtt", rewritten); // hls.js tried to parse the raw vtt as m3u8
        Assert.Contains("DEFAULT=NO", rewritten);
        Assert.Contains("AUTOSELECT=NO", rewritten);
        Assert.Contains("SUBTITLES=\"subs\"", rewritten); // stream-inf still linked to the group
    }

    // ---- B-13/B-14: the playlist wrapper itself ----

    [Fact]
    public void SubtitlePlaylist_IsACompliantSingleSegmentVodPlaylist()
    {
        var vttPath = Path.Combine(_dir, "subtitles.vtt");
        File.WriteAllText(vttPath, "WEBVTT\n");
        var transcode = new Mock<ITranscodeService>();
        transcode.Setup(t => t.GetSubtitlesVtt(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .Returns(() => File.OpenRead(vttPath));
        var svc = new StreamResultService(transcode.Object,
            new HlsManifestService(NullLogger<HlsManifestService>.Instance), NullLogger<StreamResultService>.Instance);

        var result = svc.GetSubtitlePlaylistResult(Guid.NewGuid(), Guid.NewGuid(), 2, "sid1", "tok", durationSeconds: 598);

        var content = Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", content.Content);
        Assert.Contains("#EXTINF:598.0,", content.Content);
        Assert.Contains("subtitles.vtt?token=tok&sub=2&sid=sid1", content.Content);
        Assert.Contains("#EXT-X-ENDLIST", content.Content);
    }

    [Fact]
    public void SubtitlePlaylist_404sWhenTheSessionHasNoVtt()
    {
        var transcode = new Mock<ITranscodeService>();
        transcode.Setup(t => t.GetSubtitlesVtt(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .Returns((Stream?)null);
        var svc = new StreamResultService(transcode.Object,
            new HlsManifestService(NullLogger<HlsManifestService>.Instance), NullLogger<StreamResultService>.Instance);

        var result = svc.GetSubtitlePlaylistResult(Guid.NewGuid(), Guid.NewGuid(), null, null, "tok", 100);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result);
    }

    // Live-verify repro: ffmpeg's webvtt output uses TWO-component (MM:SS.mmm)
    // timestamps and no identifiers — a far-seek offset must keep post-seek cues.
    [Fact]
    public void OffsetRewrite_KeepsPostSeekCues_InFfmpegShapedVtt()
    {
        var vttPath = Path.Combine(_dir, "ffmpeg-shape.vtt");
        var lines = new List<string> { "WEBVTT", "" };
        for (var t = 0; t < 600; t += 10)
        {
            lines.Add($"{t / 60:D2}:{t % 60:D2}.000 --> {(t + 8) / 60:D2}:{(t + 8) % 60:D2}.000");
            lines.Add($"CUE AT {t} SECONDS");
            lines.Add("");
        }
        File.WriteAllLines(vttPath, lines);

        var runner = new Mock<IProcessRunner>();
        var binaries = new Mock<IBinaryLocationService>();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_dir);
        var svc = new SubtitleService(NullLogger<SubtitleService>.Instance, runner.Object, binaries.Object, env.Object);

        Assert.True(svc.OffsetWebVttTimestamps(vttPath, 300));

        var rewritten = File.ReadAllLines(vttPath);
        Assert.DoesNotContain("CUE AT 290 SECONDS", rewritten);
        Assert.Contains("CUE AT 300 SECONDS", rewritten);
        Assert.Contains("00:00:00.000 --> 00:00:08.000", rewritten);
        Assert.Contains("CUE AT 590 SECONDS", rewritten);
    }

    // ---- B-17: offset rewrite must not leave orphan cue identifiers ----

    [Fact]
    public void OffsetRewrite_DropsTheIdentifierWithItsDroppedCue_AndKeepsSurvivorsIntact()
    {
        var vttPath = Path.Combine(_dir, "offset.vtt");
        File.WriteAllLines(vttPath, new[]
        {
            "WEBVTT",
            "",
            "cue-early",
            "00:00:05.000 --> 00:00:08.000",
            "Dropped: entirely before the seek point",
            "",
            "cue-late",
            "00:01:00.000 --> 00:01:04.000",
            "Kept: after the seek point",
            "",
        });

        var runner = new Mock<IProcessRunner>();
        var binaries = new Mock<IBinaryLocationService>();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_dir);
        var svc = new SubtitleService(NullLogger<SubtitleService>.Instance, runner.Object, binaries.Object, env.Object);

        Assert.True(svc.OffsetWebVttTimestamps(vttPath, 30));

        var rewritten = File.ReadAllLines(vttPath);
        Assert.DoesNotContain("cue-early", rewritten);   // orphan identifier gone with its cue
        Assert.Contains("cue-late", rewritten);          // surviving identifier retained…
        Assert.Contains("00:00:30.000 --> 00:00:34.000", rewritten); // …with the shifted timestamp
        Assert.Contains("WEBVTT", rewritten);            // header untouched
        Assert.DoesNotContain("Dropped: entirely before the seek point", rewritten);
    }
}
