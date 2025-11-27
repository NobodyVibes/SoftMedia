using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using System.Diagnostics;
using Xunit;

namespace SoftMedia.Tests.Services;

public class FFmpegServiceTests
{
    private readonly Mock<IProcessRunner> _processRunnerMock;
    private readonly FFmpegService _service;

    public FFmpegServiceTests()
    {
        _processRunnerMock = new Mock<IProcessRunner>();
        _service = new FFmpegService(Mock.Of<ILogger<FFmpegService>>(), _processRunnerMock.Object);
    }

    [Fact]
    public async Task ProbeMediaAsync_ReturnsCorrectMetadata()
    {
        // Arrange
        var jsonOutput = @"{
            ""streams"": [
                {
                    ""codec_type"": ""video"",
                    ""codec_name"": ""h264"",
                    ""width"": 1920,
                    ""height"": 1080
                },
                {
                    ""codec_type"": ""audio"",
                    ""codec_name"": ""aac""
                }
            ],
            ""format"": {
                ""duration"": ""120.5""
            }
        }";

        _processRunnerMock.Setup(pr => pr.RunProcessAsync(It.IsAny<ProcessStartInfo>()))
            .ReturnsAsync(jsonOutput);

        // Act
        var result = await _service.ProbeMediaAsync("test.mkv");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(120.5, result.Duration);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal("1920x1080", result.Resolution);
    }

    [Fact]
    public async Task ProbeMediaAsync_ReturnsNull_OnFailure()
    {
        // Arrange
        _processRunnerMock.Setup(pr => pr.RunProcessAsync(It.IsAny<ProcessStartInfo>()))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _service.ProbeMediaAsync("test.mkv");

        // Assert
        Assert.Null(result);
    }
}
