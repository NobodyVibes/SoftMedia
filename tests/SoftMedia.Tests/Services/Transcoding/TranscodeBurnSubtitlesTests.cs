using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Tests.Services.Transcoding;

public class TranscodeBurnSubtitlesTests
{
    private readonly Mock<ILogger<TranscodeProfileBuilder>> _loggerMock;
    private readonly Mock<IBinaryLocationService> _binaryLocationServiceMock;
    private readonly Mock<IMediaProbeService> _mediaProbeServiceMock;
    private readonly Mock<ISubtitleService> _subtitleServiceMock;
    private readonly TranscodeProfileBuilder _profileBuilder;

    public TranscodeBurnSubtitlesTests()
    {
        _loggerMock = new Mock<ILogger<TranscodeProfileBuilder>>();
        _binaryLocationServiceMock = new Mock<IBinaryLocationService>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _subtitleServiceMock = new Mock<ISubtitleService>();

        _binaryLocationServiceMock.Setup(x => x.ResolveFFmpegPath()).Returns("ffmpeg");

        _profileBuilder = new TranscodeProfileBuilder(
            _loggerMock.Object,
            _binaryLocationServiceMock.Object,
            _mediaProbeServiceMock.Object,
            _subtitleServiceMock.Object
        );
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_ForceBurnIn_OverridesTextSubtitles()
    {
        // Arrange
        var inputPath = "movie.mkv";
        var outputDir = "output";
        var segmentPrefix = "seg";
        var settings = new TranscodeSettings
        {
            EnableTranscoding = true,
            OutputVideoCodec = "h264",
            MaxResolution = "original"
        };
        int subtitleTrackIndex = 2; // Assume this is a text subtitle (SRT)

        // Mock normal SDR content
        _mediaProbeServiceMock.Setup(x => x.ProbeMediaAsync(inputPath))
            .ReturnsAsync(new MediaProbeResult 
            { 
                PixelFormat = "yuv420p", 
                ColorTransfer = "bt709" 
            });

        // Mock TEXT subtitle (Would normally use sidecar, not burn-in)
        _mediaProbeServiceMock.Setup(x => x.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex))
            .ReturnsAsync("subrip"); // SRT is text-based

        // Act
        // NOTE: TranscodeProfileBuilder itself blindly builds what it's told.
        // The decision to burn-in vs sidecar happens in TranscodeService.
        // BUT if TranscodeService decides to burn in, it calls BuildTranscodeArgumentsAsync with the subtitle index.
        // If it decides to use sidecar, it calls it with subtitleTrackIndex: null.
        
        // So this test verifies that IF `TranscodeService` passes an index (simulating forced burn-in),
        // `TranscodeProfileBuilder` generates the correct overlay filter chain, even for text subtitles.
        
        var result = await _profileBuilder.BuildTranscodeArgumentsAsync(
            inputPath, 
            outputDir, 
            segmentPrefix, 
            settings, 
            subtitleTrackIndex: subtitleTrackIndex 
        );

        // Assert
        // Should contain -vf with subtitles filter (standard for text burn-in)
        Assert.Contains("-vf", result.Arguments);
        // Should contain subtitles filter for text subs
        Assert.Contains($"subtitles='{inputPath}'", result.Arguments);
    }
}
