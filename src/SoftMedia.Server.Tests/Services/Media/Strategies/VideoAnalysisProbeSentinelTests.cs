using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Media.Strategies;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media.Strategies;

/// <summary>
/// SM-WI-043 — the Phase-2-fields backfill (BitDepth/FrameRate/Width) runs ONCE per file
/// version. Files whose probe genuinely cannot fill those fields used to re-ffprobe on
/// every scan of a stable library, forever.
/// </summary>
public class VideoAnalysisProbeSentinelTests
{
    private static (VideoAnalysisStrategy Strategy, Mock<IMediaProbeService> Probe) CreateStrategy()
    {
        var probe = new Mock<IMediaProbeService>();
        // A probe that yields no Phase-2 fields (e.g. exotic container ffprobe can't fill).
        probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { VideoCodec = "h264" });
        return (new VideoAnalysisStrategy(probe.Object, NullLogger<VideoAnalysisStrategy>.Instance), probe);
    }

    [Fact]
    public async Task MissingMode_BackfillProbesOnce_ThenSentinelStopsReprobing()
    {
        var (strategy, probe) = CreateStrategy();
        var item = new MediaItem
        {
            Title = "small soldiers",
            Type = MediaType.Movie,
            VideoCodec = "h264", // existing analysis, but Phase-2 fields missing
            BitDepth = null,
        };

        // First Missing-mode pass: backfill attempt runs and stamps the sentinel.
        await strategy.AnalyzeAsync(item, @"C:\movies\small.soldiers.1998.mkv", MetadataRefreshMode.Missing);
        Assert.NotNull(item.LastProbeAttemptUtc);
        probe.Verify(p => p.ProbeMediaAsync(It.IsAny<string>()), Times.Once);

        // Second pass (still no BitDepth — this file can't provide it): no re-probe.
        await strategy.AnalyzeAsync(item, @"C:\movies\small.soldiers.1998.mkv", MetadataRefreshMode.Missing);
        probe.Verify(p => p.ProbeMediaAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FullMode_AlwaysReprobes_RegardlessOfSentinel()
    {
        var (strategy, probe) = CreateStrategy();
        var item = new MediaItem
        {
            Title = "small soldiers",
            Type = MediaType.Movie,
            VideoCodec = "h264",
            LastProbeAttemptUtc = DateTime.UtcNow, // sentinel present
        };

        // Changed files route through Full mode — the sentinel must not block them.
        await strategy.AnalyzeAsync(item, @"C:\movies\small.soldiers.1998.mkv", MetadataRefreshMode.Full);
        probe.Verify(p => p.ProbeMediaAsync(It.IsAny<string>()), Times.Once);
    }
}
