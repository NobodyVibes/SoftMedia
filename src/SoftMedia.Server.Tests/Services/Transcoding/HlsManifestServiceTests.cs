using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// SR-WI-028: manifest rewriting must append the auth query per-URL, line-based.
/// The old blanket string.Replace(".ts", ...) ran over the whole manifest text —
/// including the subtitle-playlist URI whose JWT can contain ".ts" in its base64url
/// signature — corrupting every URL for that playback.
public class HlsManifestServiceTests : IDisposable
{
    // Fake JWT whose "signature" segment contains ".ts" — the ~1-in-2500 token that
    // the old blanket Replace corrupted.
    private const string TokenWithTs = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc.tsXYZdef";

    private readonly HlsManifestService _svc = new(NullLogger<HlsManifestService>.Instance);
    private readonly string _vttPath;

    public HlsManifestServiceTests()
    {
        _vttPath = Path.Combine(Path.GetTempPath(), "sm-hls-tests-" + Guid.NewGuid().ToString("N") + ".vtt");
        File.WriteAllText(_vttPath, "WEBVTT\n");
    }

    public void Dispose()
    {
        try { File.Delete(_vttPath); } catch { }
    }

    private static Stream AsStream(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static string[] Lines(byte[] output) =>
        Encoding.UTF8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task SegmentLines_GetQueryAppended_CommentLinesUntouched()
    {
        var playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI=\"init.mp4\"\n" +
                       "#EXTINF:4.000,\nseg-0.ts\n#EXTINF:4.000,\nseg-1.m4s\n#EXT-X-ENDLIST\n";

        var output = await _svc.GenerateMasterPlaylistAsync(AsStream(playlist), "tok123", "m1", null, null);
        var lines = Lines(output);

        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4?token=tok123\"", lines);
        Assert.Contains("seg-0.ts?token=tok123", lines);
        Assert.Contains("seg-1.m4s?token=tok123", lines);
        // #EXTINF durations contain no segment URL — must stay pristine.
        Assert.Contains("#EXTINF:4.000,", lines);
        Assert.Contains("#EXT-X-VERSION:7", lines);
        Assert.Contains("#EXT-X-ENDLIST", lines);
    }

    [Fact]
    public async Task TokenContainingDotTs_DoesNotCorruptAnyUrl()
    {
        // Regression: subtitle rendition active, so the manifest carries a URI with
        // the JWT in its query string; the token's signature contains ".ts".
        var playlist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\n" +
                       "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:4.000,\nseg-0.ts\n";

        var output = await _svc.GenerateMasterPlaylistAsync(
            AsStream(playlist), TokenWithTs, "media-1", subTrackIndex: 2, subtitleVttPath: _vttPath, sid: "s9");
        var text = Encoding.UTF8.GetString(output);
        var lines = Lines(output);

        // The subtitle playlist URI must carry the token verbatim — the old code
        // spliced ".ts?token=..." into the middle of the JWT here.
        Assert.Contains(
            $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles\",DEFAULT=NO,AUTOSELECT=NO,URI=\"/api/v1/transcode/media-1/subtitles.m3u8?token={TokenWithTs}&sub=2&sid=s9\"",
            lines);

        // Segment URLs get exactly one intact query appended.
        Assert.Contains($"seg-0.ts?token={TokenWithTs}&sub=2&sid=s9", lines);
        Assert.Contains($"#EXT-X-MAP:URI=\"init.mp4?token={TokenWithTs}&sub=2&sid=s9\"", lines);

        // No URL anywhere was corrupted by splicing a query into the token itself.
        Assert.DoesNotContain(".ts?token=" + TokenWithTs[..10], text.Replace($"seg-0.ts?token={TokenWithTs}", ""));
        Assert.Contains("SUBTITLES=\"subs\"", text); // stream-inf linkage still applied
    }

    [Fact]
    public void AppendQueryToSegmentUrls_LeavesUrisWithExistingQueryAlone()
    {
        var manifest = "#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"/api/subtitles.m3u8?token=a.tsb\"\nseg-0.ts\n";

        var result = HlsManifestService.AppendQueryToSegmentUrls(manifest, "token=x");

        Assert.Contains("URI=\"/api/subtitles.m3u8?token=a.tsb\"", result); // untouched
        Assert.Contains("seg-0.ts?token=x", result);
    }

    [Fact]
    public void AppendQueryToSegmentUrls_OnlyTransformsLinesEndingWithSegmentExtension()
    {
        // A line merely CONTAINING ".ts" mid-string (e.g. a variant playlist name)
        // must not be rewritten.
        var manifest = "variant.tsx.m3u8\nseg-1.ts\nseg-2.m4s\ninit.mp4\n";

        var result = HlsManifestService.AppendQueryToSegmentUrls(manifest, "token=x");
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { "variant.tsx.m3u8", "seg-1.ts?token=x", "seg-2.m4s?token=x", "init.mp4?token=x" }, lines);
    }

    [Fact]
    public void AppendQueryToSegmentUrls_EmptyQuery_ReturnsManifestUnchanged()
    {
        var manifest = "#EXTM3U\nseg-0.ts\n";
        Assert.Equal(manifest, HlsManifestService.AppendQueryToSegmentUrls(manifest, ""));
    }
}
