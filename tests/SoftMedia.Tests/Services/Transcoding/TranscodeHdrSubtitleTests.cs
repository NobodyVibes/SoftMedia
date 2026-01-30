using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Tests.Services.Transcoding;

public class TranscodeHdrSubtitleTests
{
    private readonly Mock<ILogger<TranscodeProfileBuilder>> _loggerMock;
    private readonly Mock<IBinaryLocationService> _binaryLocationServiceMock;
    private readonly Mock<IMediaProbeService> _mediaProbeServiceMock;
    private readonly Mock<ISubtitleService> _subtitleServiceMock;
    private readonly TranscodeProfileBuilder _profileBuilder;

    public TranscodeHdrSubtitleTests()
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
    public async Task BuildTranscodeArgumentsAsync_HdrWithSubtitles_Nvidia_CurrentlyBypassesToneMapping()
    {
        // Arrange
        var inputPath = "hdr_movie.mkv";
        var outputDir = "output";
        var segmentPrefix = "seg";
        var settings = new TranscodeSettings
        {
            EnableTranscoding = true,
            OutputVideoCodec = "h264",
            HardwareAcceleration = "nvidia",
            MaxResolution = "original",
            ToneMappingAlgorithm = "hable"
        };
        int subtitleTrackIndex = 2;

        // Mock HDR probe results
        _mediaProbeServiceMock.Setup(x => x.ProbeMediaAsync(inputPath))
            .ReturnsAsync(new MediaProbeResult 
            { 
                PixelFormat = "yuv420p10le", 
                ColorTransfer = "smpte2084" // HDR
            });

        // Mock bitmap subtitle (PGS)
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

        // Assert - FIXED BEHAVIOR EXPECTATION
        // We expect tone mapping to be present
        Assert.Contains("tonemap_cuda", result.Arguments);
        
        // We expect hardware download to be present (needed for subtitle overlay on CPU)
        Assert.Contains("hwdownload", result.Arguments);
        Assert.Contains("format=nv12", result.Arguments);
        
        // We expect scale2ref to be present (subtitle burning)
        Assert.Contains("scale2ref", result.Arguments);
    }
}
