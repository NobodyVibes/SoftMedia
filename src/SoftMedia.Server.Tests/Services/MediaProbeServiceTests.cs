using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Services;

public class MediaProbeServiceTests
{
    private readonly Mock<ILogger<MediaProbeService>> _loggerMock;
    private readonly Mock<IProcessRunner> _processRunnerMock;
    private readonly Mock<IBinaryLocationService> _binaryLocationServiceMock;
    private readonly MediaProbeService _service;

    public MediaProbeServiceTests()
    {
        _loggerMock = new Mock<ILogger<MediaProbeService>>();
        _processRunnerMock = new Mock<IProcessRunner>();
        _binaryLocationServiceMock = new Mock<IBinaryLocationService>();
        
        _binaryLocationServiceMock.Setup(x => x.ResolveFFprobePath()).Returns("ffprobe_mock");

        _service = new MediaProbeService(
            _loggerMock.Object,
            _processRunnerMock.Object,
            _binaryLocationServiceMock.Object
        );
    }

    [Fact]
    public async Task ProbeMediaAsync_ReturnsNull_WhenProcessFails()
    {
        // Arrange
        _processRunnerMock
            .Setup(x => x.RunProcessAsync(It.IsAny<System.Diagnostics.ProcessStartInfo>()))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _service.ProbeMediaAsync("test.mp4");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ProbeMediaAsync_ParsesValidJsonCorrectly()
    {
        // Arrange
        var jsonOutput = @"{
            ""format"": {
                ""duration"": ""120.5""
            },
            ""streams"": [
                {
                    ""codec_type"": ""video"",
                    ""codec_name"": ""h264"",
                    ""width"": 1920,
                    ""height"": 1080,
                    ""pix_fmt"": ""yuv420p"",
                    ""avg_frame_rate"": ""24/1""
                },
                {
                    ""codec_type"": ""audio"",
                    ""codec_name"": ""aac""
                }
            ],
            ""chapters"": []
        }";

        _processRunnerMock
            .Setup(x => x.RunProcessAsync(It.Is<System.Diagnostics.ProcessStartInfo>(p => p.Arguments.Contains("test.mp4"))))
            .ReturnsAsync(jsonOutput);

        // Act
        var result = await _service.ProbeMediaAsync("test.mp4");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(120.5, result.Duration);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal("1920x1080", result.Resolution);
        Assert.Equal(24.0, result.FrameRate);
        Assert.Equal("yuv420p", result.PixelFormat);
    }
}
