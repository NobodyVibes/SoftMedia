using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Services;

public class TranscodeProfileBuilderTests
{
    private readonly Mock<ILogger<TranscodeProfileBuilder>> _loggerMock;
    private readonly Mock<IBinaryLocationService> _binaryMock;
    private readonly Mock<IMediaProbeService> _probeMock;
    private readonly Mock<ISubtitleService> _subtitleMock;
    private readonly TranscodeProfileBuilder _builder;

    public TranscodeProfileBuilderTests()
    {
        _loggerMock = new Mock<ILogger<TranscodeProfileBuilder>>();
        _binaryMock = new Mock<IBinaryLocationService>();
        _probeMock = new Mock<IMediaProbeService>();
        _subtitleMock = new Mock<ISubtitleService>();

        _binaryMock.Setup(x => x.ResolveFFmpegPath()).Returns("ffmpeg");
        _binaryMock.Setup(x => x.ResolveFFprobePath()).Returns("ffprobe");

        _builder = new TranscodeProfileBuilder(
            _loggerMock.Object,
            _binaryMock.Object,
            _probeMock.Object,
            _subtitleMock.Object
        );
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_BasicSettings_ReturnsLibx264()
    {
        // Arrange
        var settings = new TranscodeSettings
        {
            HardwareAcceleration = "none",
            Preset = "fast",
            CRF = 23,
            OutputVideoCodec = "h264"
        };
        
        _probeMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" });

        // Act
        var result = await _builder.BuildTranscodeArgumentsAsync("input.mkv", "output", "seg", settings);

        // Assert
        Assert.Contains("-c:v libx264", result.Arguments);
        Assert.Contains("-preset fast", result.Arguments);
        Assert.Contains("-crf 23", result.Arguments);
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_NvidiaHevc_ReturnsNvenc()
    {
        // Arrange
        var settings = new TranscodeSettings
        {
            HardwareAcceleration = "nvidia",
            OutputVideoCodec = "hevc",
            Preset = "slow"
        };

        _probeMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" });

        // Act
        var result = await _builder.BuildTranscodeArgumentsAsync("input.mkv", "output", "seg", settings);

        // Assert
        Assert.Contains("-hwaccel cuda", result.Arguments);
        Assert.Contains("-c:v hevc_nvenc", result.Arguments);
        // "slow" maps to "p5" for nvenc
        Assert.Contains("-preset p5", result.Arguments);
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_WithSeek_AddsSsArgument()
    {
        // Arrange
        var settings = new TranscodeSettings();
        _probeMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" });

        // Act
        var result = await _builder.BuildTranscodeArgumentsAsync("input.mkv", "output", "seg", settings, seekPosition: 120.5);

        // Assert
        Assert.Contains("-ss 120.50", result.Arguments);
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_SubtitleOverlay_ExtractsAndFilters()
    {
        // Arrange
        var settings = new TranscodeSettings();
        _probeMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" });
        
        _probeMock.Setup(x => x.ProbeSubtitleCodecAsync(It.IsAny<string>(), 2))
            .ReturnsAsync("subrip"); // Text subtitle
            
        _subtitleMock.Setup(x => x.GetSubtitleStreamIndexAsync(It.IsAny<string>(), 2))
            .ReturnsAsync(0);

        // Act
        var result = await _builder.BuildTranscodeArgumentsAsync("input.mkv", "output", "seg", settings, subtitleTrackIndex: 2);

        // Assert
        // Should use subtitles filter for text subtitles
        Assert.Contains("subtitles=", result.Arguments);
        Assert.DoesNotContain("overlay", result.Arguments);
    }
    
    [Fact]
    public async Task BuildTranscodeArgumentsAsync_Av1_UsesFmp4()
    {
        // Arrange
        var settings = new TranscodeSettings { OutputVideoCodec = "av1" };
        _probeMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" });

        // Act
        var result = await _builder.BuildTranscodeArgumentsAsync("input.mkv", "output", "seg", settings);

        // Assert
        Assert.Contains("-hls_segment_type fmp4", result.Arguments);
        Assert.Contains("init.mp4", result.Arguments);
    }
}
