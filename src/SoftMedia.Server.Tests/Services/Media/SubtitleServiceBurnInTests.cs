using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// R-WI-012 — the burn-in extractor. Success REQUIRES ffmpeg exit code 0 plus a non-empty output;
/// a killed/failed run's partial .ass must be deleted, never burned (review finding: a truncated
/// file that "looks fine" makes subtitles silently vanish mid-movie). Font attachments are dumped
/// under sanitized names only (file-supplied names could path-traverse).
public class SubtitleServiceBurnInTests : IDisposable
{
    private readonly Mock<IProcessRunner> _runner = new();
    private readonly string _dir;
    private readonly string _outputPath;

    public SubtitleServiceBurnInTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _outputPath = Path.Combine(_dir, "burnin.ass");
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
        return new SubtitleService(NullLogger<SubtitleService>.Instance, _runner.Object, binaries.Object);
    }

    private void SetupExtraction(int exitCode, bool writesFile, string content = "[Script Info]\nTitle: x")
    {
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .Callback<ProcessStartInfo, TimeSpan>((psi, _) =>
            {
                if (writesFile) File.WriteAllText(_outputPath, content);
            })
            .ReturnsAsync(exitCode);
    }

    // ---- ExtractSubtitleToAssAsync ----

    [Fact]
    public async Task CleanExit_with_output_succeeds_and_uses_ass_codec_with_quoted_paths()
    {
        ProcessStartInfo? captured = null;
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .Callback<ProcessStartInfo, TimeSpan>((psi, _) => { captured = psi; File.WriteAllText(_outputPath, "[Script Info]"); })
            .ReturnsAsync(0);

        var inputPath = @"C:\media\It's a Clip (2024)\It's a Clip.mkv";
        var ok = await Service().ExtractSubtitleToAssAsync(inputPath, 1, _outputPath);

        Assert.True(ok);
        Assert.Contains($"-i \"{inputPath}\"", captured!.Arguments); // quoted -i arg, apostrophes fine here
        Assert.Contains("-map 0:s:1", captured.Arguments);           // subtitle-relative index
        Assert.Contains("-c:s ass", captured.Arguments);             // ASS, not WebVTT — keeps styling
    }

    [Fact]
    public async Task NonzeroExit_with_partial_output_fails_and_deletes_the_partial()
    {
        // The truncated-burn scenario: ffmpeg died/was killed after flushing some dialogue lines.
        SetupExtraction(exitCode: 1, writesFile: true, content: "[Script Info]\nDialogue: partial…");

        var ok = await Service().ExtractSubtitleToAssAsync(@"C:\media\big.mkv", 0, _outputPath);

        Assert.False(ok);
        Assert.False(File.Exists(_outputPath)); // partial must never survive to be burned
    }

    [Fact]
    public async Task Timeout_kill_with_partial_output_fails_and_deletes_the_partial()
    {
        SetupExtraction(exitCode: -1, writesFile: true); // -1 = RunProcessForExitCodeAsync timeout kill

        var ok = await Service().ExtractSubtitleToAssAsync(@"C:\media\huge-remux.mkv", 0, _outputPath);

        Assert.False(ok);
        Assert.False(File.Exists(_outputPath));
    }

    [Fact]
    public async Task CleanExit_without_output_fails()
    {
        SetupExtraction(exitCode: 0, writesFile: false);
        Assert.False(await Service().ExtractSubtitleToAssAsync(@"C:\media\in.mkv", 0, _outputPath));
    }

    [Fact]
    public async Task Runner_throw_fails_gracefully()
    {
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg missing"));
        Assert.False(await Service().ExtractSubtitleToAssAsync(@"C:\media\in.mkv", 0, _outputPath)); // never throws into the transcode path
    }

    // ---- DumpFontAttachmentsAsync ----

    private const string ProbeJsonWithFont = """
        { "streams": [
            { "index": 0, "codec_type": "video" },
            { "index": 3, "codec_type": "attachment", "tags": { "mimetype": "application/x-truetype-font", "filename": "..\\..\\evil.ttf" } },
            { "index": 4, "codec_type": "attachment", "tags": { "mimetype": "image/jpeg", "filename": "cover.jpg" } }
        ] }
        """;

    [Fact]
    public async Task Dumps_fonts_under_sanitized_names_ignoring_attachment_filename_metadata()
    {
        _runner.Setup(r => r.RunProcessAsync(It.IsAny<ProcessStartInfo>())).ReturnsAsync(ProbeJsonWithFont);
        ProcessStartInfo? dump = null;
        _runner.Setup(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
            .Callback<ProcessStartInfo, TimeSpan>((psi, _) =>
            {
                dump = psi;
                File.WriteAllBytes(Path.Combine(_dir, "font0.ttf"), new byte[] { 1, 2, 3 });
            })
            .ReturnsAsync(1); // ffmpeg exits non-zero after -dump_attachment (no mapped output) — expected

        var count = await Service().DumpFontAttachmentsAsync(@"C:\media\anime.mkv", _dir);

        Assert.Equal(1, count); // only the FONT attachment, not the jpeg
        Assert.Contains("-dump_attachment:3", dump!.Arguments);        // absolute stream index
        Assert.Contains("font0.ttf", dump.Arguments);                  // OUR name…
        Assert.DoesNotContain("evil", dump.Arguments);                 // …never the file's traversal name
    }

    [Fact]
    public async Task No_font_attachments_returns_zero_without_dumping()
    {
        _runner.Setup(r => r.RunProcessAsync(It.IsAny<ProcessStartInfo>()))
            .ReturnsAsync("""{ "streams": [ { "index": 0, "codec_type": "video" } ] }""");

        Assert.Equal(0, await Service().DumpFontAttachmentsAsync(@"C:\media\plain.mkv", _dir));
        _runner.Verify(r => r.RunProcessForExitCodeAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()), Times.Never);
    }
}
