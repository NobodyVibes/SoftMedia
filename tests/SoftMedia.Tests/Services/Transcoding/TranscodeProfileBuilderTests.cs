using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Tests.Services.Transcoding;

public class TranscodeProfileBuilderTests
{
    private readonly Mock<ILogger<TranscodeProfileBuilder>> _loggerMock;
    private readonly Mock<IBinaryLocationService> _binaryLocationServiceMock;
    private readonly Mock<IMediaProbeService> _mediaProbeServiceMock;
    private readonly Mock<ISubtitleService> _subtitleServiceMock;
    private readonly TranscodeProfileBuilder _profileBuilder;

    public TranscodeProfileBuilderTests()
    {
        _loggerMock = new Mock<ILogger<TranscodeProfileBuilder>>();
        _binaryLocationServiceMock = new Mock<IBinaryLocationService>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _subtitleServiceMock = new Mock<ISubtitleService>();

        // Setup default binary paths
        _binaryLocationServiceMock.Setup(x => x.ResolveFFmpegPath()).Returns("ffmpeg");

        _profileBuilder = new TranscodeProfileBuilder(
            _loggerMock.Object,
            _binaryLocationServiceMock.Object,
            _mediaProbeServiceMock.Object,
            _subtitleServiceMock.Object
        );
    }

    [Fact]
    public async Task BuildTranscodeArgumentsAsync_BitmapSubtitle_UsesScale2Ref()
    {
        // Arrange
        var inputPath = "input.mkv";
        var outputDir = "output";
        var segmentPrefix = "seg";
        var settings = new TranscodeSettings
        {
            EnableTranscoding = true,
            OutputVideoCodec = "libx264",
            MaxResolution = "original"
        };
        int subtitleTrackIndex = 2; // Arbitrary index

        // Setup mocks
        _mediaProbeServiceMock.Setup(x => x.ProbeMediaAsync(inputPath))
            .ReturnsAsync(new MediaProbeResult { PixelFormat = "yuv420p" }); // SDR

        // Mock bitmap codec (e.g. PGS)
        _mediaProbeServiceMock.Setup(x => x.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex))
            .ReturnsAsync("hdmv_pgs_subtitle");
            
        // Act
        var result = await _profileBuilder.BuildTranscodeArgumentsAsync(
            inputPath, 
            outputDir, 
            segmentPrefix, 
            settings, 
            subtitleTrackIndex: subtitleTrackIndex
        );

        // Assert
        Assert.Contains("scale2ref=flags=bicubic", result.Arguments);
        Assert.Contains("[0:2][0:v]scale2ref=flags=bicubic[subs][vid]", result.Arguments);
        Assert.Contains("[vid][subs]overlay", result.Arguments);
    }
}
