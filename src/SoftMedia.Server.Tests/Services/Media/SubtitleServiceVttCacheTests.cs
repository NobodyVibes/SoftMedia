using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// SR-WI-022 — the sidecar VTT extractor. Parity with the burn-in extractor (R-WI-012):
/// success REQUIRES ffmpeg exit code 0 plus a non-empty output via the exit-code-strict
/// runner (10-minute backstop, not the 30s RunProcessAsync kill); a killed/failed run's
/// partial .vtt is deleted, never served. Extractions are cached persistently under
/// wwwroot/cache/subtitles keyed by (source path, track, mtime); the cache holds the
/// UNSHIFTED VTT and every caller gets its own copy (TranscodeService seek-shifts the copy).
public class SubtitleServiceVttCacheTests : IDisposable
{
    private const string VttContent = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHello\n";

    private readonly Mock<IProcessRunner> _runner = new();
    private readonly string _dir;
    private readonly string _inputPath;
    private readonly string _outputPath;
    private readonly string _cacheDir;

    public SubtitleServiceVttCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _inputPath = Path.Combine(_dir, "movie.mkv");
        File.WriteAllText(_inputPath, "fake container");
        _outputPath = Path.Combine(_dir, "session1", "subtitles.vtt");
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        _cacheDir = Path.Combine(_dir, "cache", "subtitles");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SubtitleService Service()
    {
        var binaries = new Mock<IBinaryLocationService>();
        binaries.Setup(b => b.ResolveFFmpegPath()).Returns("ffmpeg");
        binaries.Setup(b => b.ResolveFFprobePath()).Returns("ffprobe");
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_dir);
        return new SubtitleService(NullLogger<SubtitleService>.Instance, _runner.Object, binaries.Object, env.Object);
    }

    /// The service extracts to a temp path of its own choosing (inside the cache dir), so the
    /// fake ffmpeg writes to the -y output path parsed from the argument string.
    private static string OutputPathFromArgs(string arguments)
    {
        var marker = "-y \"";
        var start = arguments.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return arguments.Substring(start, arguments.Length - start - 1);
    }

    private void SetupExtraction(int exitCode, bool writesFile, string content = VttContent)
    {
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .Callback<ProcessStartInfo, TimeSpan>((psi, _) =>
            {
                if (writesFile) File.WriteAllText(OutputPathFromArgs(psi.Arguments), content);
            })
            .ReturnsAsync(exitCode);
    }

    // ---- parity with the burn-in extractor: exit-code-strict, partials deleted ----

    [Fact]
    public async Task CleanExit_with_output_succeeds_uses_webvtt_codec_and_the_ten_minute_timeout()
    {
        ProcessStartInfo? captured = null;
        TimeSpan? timeout = null;
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .Callback<ProcessStartInfo, TimeSpan>((psi, t) =>
            {
                captured = psi;
                timeout = t;
                File.WriteAllText(OutputPathFromArgs(psi.Arguments), VttContent);
            })
            .ReturnsAsync(0);

        var ok = await Service().ExtractSubtitleToVttAsync(_inputPath, 2, _outputPath);

        Assert.True(ok);
        Assert.Equal(VttContent, File.ReadAllText(_outputPath));
        Assert.Contains($"-i \"{_inputPath}\"", captured!.Arguments);
        Assert.Contains("-map 0:s:2", captured.Arguments);            // subtitle-relative index
        Assert.Contains("-c:s webvtt", captured.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(10), timeout);              // hung-source backstop, not a 30s kill
        _runner.Verify(r => r.RunProcessAsync(It.IsAny<ProcessStartInfo>()), Times.Never); // never the exit-code-blind runner
    }

    [Fact]
    public async Task NonzeroExit_with_partial_output_fails_deletes_the_partial_and_caches_nothing()
    {
        // The truncated-remux scenario: ffmpeg died/was killed after flushing early cues.
        SetupExtraction(exitCode: 1, writesFile: true, content: "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\npartial…\n");

        var ok = await Service().ExtractSubtitleToVttAsync(_inputPath, 0, _outputPath);

        Assert.False(ok);
        Assert.False(File.Exists(_outputPath)); // the session target never sees a partial
        if (Directory.Exists(_cacheDir))
            Assert.Empty(Directory.GetFiles(_cacheDir)); // no partial promoted, no temp left behind
    }

    [Fact]
    public async Task Timeout_kill_with_partial_output_fails_and_deletes_the_partial()
    {
        SetupExtraction(exitCode: -1, writesFile: true); // -1 = RunProcessForExitCodeAsync timeout kill

        var ok = await Service().ExtractSubtitleToVttAsync(_inputPath, 0, _outputPath);

        Assert.False(ok);
        Assert.False(File.Exists(_outputPath));
        if (Directory.Exists(_cacheDir))
            Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [Fact]
    public async Task CleanExit_without_output_fails()
    {
        SetupExtraction(exitCode: 0, writesFile: false);
        Assert.False(await Service().ExtractSubtitleToVttAsync(_inputPath, 0, _outputPath));
    }

    [Fact]
    public async Task Runner_throw_fails_gracefully()
    {
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg missing"));
        Assert.False(await Service().ExtractSubtitleToVttAsync(_inputPath, 0, _outputPath)); // never throws into the transcode path
    }

    // ---- the persistent cache ----

    [Fact]
    public async Task Second_extraction_for_same_source_track_and_mtime_is_served_from_cache()
    {
        SetupExtraction(exitCode: 0, writesFile: true);
        var svc = Service();

        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, _outputPath));

        var secondTarget = Path.Combine(_dir, "session2", "subtitles.vtt");
        Directory.CreateDirectory(Path.GetDirectoryName(secondTarget)!);
        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, secondTarget));

        Assert.Equal(VttContent, File.ReadAllText(secondTarget));
        _runner.Verify(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()),
            Times.Once); // demuxed once — the far-seek re-request hit the cache
    }

    [Fact]
    public async Task Cache_serves_the_unshifted_vtt_even_after_a_session_seek_shifts_its_copy()
    {
        // TranscodeService applies OffsetWebVttTimestamps to the SESSION copy after extraction;
        // the cache must keep the extraction-fresh cues so the next seek shifts from zero.
        SetupExtraction(exitCode: 0, writesFile: true);
        var svc = Service();

        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, _outputPath));
        Assert.True(svc.OffsetWebVttTimestamps(_outputPath, 0.5)); // session 1 seek-shifts its copy in place

        var secondTarget = Path.Combine(_dir, "session2", "subtitles.vtt");
        Directory.CreateDirectory(Path.GetDirectoryName(secondTarget)!);
        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, secondTarget));

        Assert.Contains("00:00:01.000 --> 00:00:03.000", File.ReadAllText(secondTarget)); // pristine, not shifted
    }

    [Fact]
    public async Task Different_track_of_the_same_source_is_not_a_cache_hit()
    {
        SetupExtraction(exitCode: 0, writesFile: true);
        var svc = Service();

        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 0, _outputPath));
        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, _outputPath));

        _runner.Verify(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Mtime_change_bypasses_the_stale_cache_and_evicts_the_old_variant()
    {
        SetupExtraction(exitCode: 0, writesFile: true, content: "WEBVTT\n\nold cues\n");
        var svc = Service();
        File.SetLastWriteTimeUtc(_inputPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, _outputPath));
        Assert.Single(Directory.GetFiles(_cacheDir, "*.vtt"));

        // The source is re-muxed/upgraded: same path, new mtime — the old cache entry is stale.
        SetupExtraction(exitCode: 0, writesFile: true, content: "WEBVTT\n\nnew cues\n");
        File.SetLastWriteTimeUtc(_inputPath, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(await svc.ExtractSubtitleToVttAsync(_inputPath, 1, _outputPath));

        Assert.Contains("new cues", File.ReadAllText(_outputPath)); // fresh extraction, not the stale hit
        _runner.Verify(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()),
            Times.Exactly(2));
        Assert.Single(Directory.GetFiles(_cacheDir, "*.vtt")); // stale variant evicted, only the new mtime remains
    }
}
