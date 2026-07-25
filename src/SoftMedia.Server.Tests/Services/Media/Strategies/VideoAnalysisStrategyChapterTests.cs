using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Media.Strategies;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media.Strategies;

/// <summary>
/// CM-WI-002: the scan path's chapter-marker invariant — Chapter-sourced timecodes mirror
/// the file's current chapters (written on match, cleared on marker disappearance), and
/// Detected-sourced values are never touched by the strategy.
/// </summary>
public class VideoAnalysisStrategyChapterTests
{
    private readonly Mock<IMediaProbeService> _probe = new();
    private readonly VideoAnalysisStrategy _strategy;

    public VideoAnalysisStrategyChapterTests()
    {
        _strategy = new VideoAnalysisStrategy(_probe.Object, NullLogger<VideoAnalysisStrategy>.Instance);
    }

    private static MediaProbeResult ProbeResult(double duration, params (double, string)[] chapters) => new()
    {
        Duration = duration,
        VideoCodec = "hevc",
        Chapters = chapters.Length > 0 ? chapters.ToList() : null,
    };

    private static MediaItem Episode() => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaType.Episode,
        Title = "Ep",
        Path = @"C:\tv\ep.mkv",
    };

    private async Task<MediaItem> AnalyzeAsync(MediaItem item, MediaProbeResult probe)
    {
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(probe);
        // Full mode probes unconditionally, so no real file is needed.
        Assert.True(await _strategy.AnalyzeAsync(item, item.Path, MetadataRefreshMode.Full));
        return item;
    }

    [Fact]
    public async Task ChapterMarkers_WriteAllFourTimecodes_WithChapterSource()
    {
        var item = await AnalyzeAsync(Episode(),
            ProbeResult(1486.736, (0, "Intro"), (32.324, "Scene 1"), (1437.853, "Credits")));

        Assert.Equal(0, item.IntroStart);
        Assert.Equal(32.324, item.IntroEnd);
        Assert.Equal(DetectionSource.Chapter, item.IntroSource);
        Assert.Equal(1437.853, item.CreditsStart);
        Assert.Equal(1486.736, item.CreditsEnd);
        Assert.Equal(DetectionSource.Chapter, item.CreditsSource);
    }

    [Fact]
    public async Task ChapterMarkers_OverrideDetectedValues_ChapterIsGroundTruth()
    {
        var item = Episode();
        item.IntroStart = 9.66; item.IntroEnd = 34.79; item.IntroSource = DetectionSource.Detected;

        await AnalyzeAsync(item, ProbeResult(1486.736, (0, "Intro"), (32.324, "Scene 1")));

        Assert.Equal(0, item.IntroStart);
        Assert.Equal(DetectionSource.Chapter, item.IntroSource);
    }

    [Fact]
    public async Task DetectedValues_AreUntouched_WhenChaptersDontMatch()
    {
        var item = Episode();
        item.IntroStart = 9.66; item.IntroEnd = 34.79; item.IntroSource = DetectionSource.Detected;
        item.CreditsStart = 1400; item.CreditsEnd = 1450; item.CreditsSource = DetectionSource.Detected;

        await AnalyzeAsync(item, ProbeResult(1486.736, (0, "Chapter 1"), (700, "Chapter 2")));

        Assert.Equal(9.66, item.IntroStart);
        Assert.Equal(DetectionSource.Detected, item.IntroSource);
        Assert.Equal(1400, item.CreditsStart);
        Assert.Equal(DetectionSource.Detected, item.CreditsSource);
    }

    [Fact]
    public async Task StaleChapterMarkers_AreCleared_WhenFileNoLongerHasThem()
    {
        // The file was replaced with a cut whose chapters are generic — the old
        // chapter-derived markers must not survive as stale skip targets. Detection
        // can re-fill them later.
        var item = Episode();
        item.IntroStart = 0; item.IntroEnd = 32; item.IntroSource = DetectionSource.Chapter;
        item.CreditsStart = 1437; item.CreditsEnd = 1486; item.CreditsSource = DetectionSource.Chapter;

        await AnalyzeAsync(item, ProbeResult(1486.736, (0, "Chapter 1"), (700, "Chapter 2")));

        Assert.Null(item.IntroStart);
        Assert.Null(item.IntroEnd);
        Assert.Null(item.IntroSource);
        Assert.Null(item.CreditsStart);
        Assert.Null(item.CreditsEnd);
        Assert.Null(item.CreditsSource);
    }

    [Fact]
    public async Task ChapterlessFile_ClearsChapterSourced_ButKeepsDetected()
    {
        var item = Episode();
        item.IntroStart = 0; item.IntroEnd = 32; item.IntroSource = DetectionSource.Chapter;
        item.CreditsStart = 1400; item.CreditsEnd = 1450; item.CreditsSource = DetectionSource.Detected;

        await AnalyzeAsync(item, ProbeResult(1486.736 /* no chapters */));

        Assert.Null(item.IntroSource);          // chapter-sourced intro cleared
        Assert.Equal(1400, item.CreditsStart);  // detected credits untouched
        Assert.Equal(DetectionSource.Detected, item.CreditsSource);
    }
}
