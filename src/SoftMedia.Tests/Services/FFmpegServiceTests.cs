using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using System.Diagnostics;
using Xunit;

namespace SoftMedia.Tests.Services;

public class FFmpegServiceTests
{
    private readonly Mock<IProcessRunner> _processRunnerMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<ILogger<FFmpegService>> _loggerMock;
    private readonly Mock<IMediaProbeService> _mediaProbeServiceMock;
    private readonly Mock<ISubtitleService> _subtitleServiceMock;
    private readonly Mock<ITranscodeProfileBuilder> _transcodeProfileBuilderMock;
    
    private readonly string _testInputPath = "C:\\test\\input.mkv";
    private readonly string _testOutputDir = "C:\\test\\output";
    private readonly string _testSegmentPrefix = "segment";

    public FFmpegServiceTests()
    {
        _processRunnerMock = new Mock<IProcessRunner>();
        _settingsServiceMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<FFmpegService>>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _subtitleServiceMock = new Mock<ISubtitleService>();
        _transcodeProfileBuilderMock = new Mock<ITranscodeProfileBuilder>();
        
        // Setup default settings
        SetupDefaultSettings();
    }

    private void SetupDefaultSettings()
    {
        _settingsServiceMock.Setup(s => s.GetSettingAsync("EnableTranscoding", "true")).ReturnsAsync("true");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("none");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodePreset", "veryfast")).ReturnsAsync("veryfast");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeThreadCount", "0")).ReturnsAsync("0");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("original");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeCRF", "23")).ReturnsAsync("23");
    }

    private FFmpegService CreateService()
    {
        return new FFmpegService(
            _loggerMock.Object, 
            _settingsServiceMock.Object,
            _mediaProbeServiceMock.Object,
            _subtitleServiceMock.Object,
            _transcodeProfileBuilderMock.Object
        );
    }

    #region ProbeMediaAsync Tests

    [Fact]
    public async Task ProbeMediaAsync_ReturnsCorrectMetadata()
    {
        // Arrange
        var expectedProbe = new MediaProbeResult
        {
            Duration = 120.5,
            VideoCodec = "h264",
            AudioCodec = "aac",
            Width = 1920,
            Height = 1080
        };

        _mediaProbeServiceMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync(expectedProbe);

        var service = CreateService();

        // Act
        var result = await service.ProbeMediaAsync(_testInputPath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(120.5, result.Duration);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public async Task ProbeMediaAsync_ReturnsNull_OnFailure()
    {
        // Arrange
        _mediaProbeServiceMock.Setup(x => x.ProbeMediaAsync(It.IsAny<string>()))
            .ReturnsAsync((MediaProbeResult?)null);

        var service = CreateService();

        // Act
        var result = await service.ProbeMediaAsync("test.mkv");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region IsTranscodingDisabledAsync Tests

    [Fact]
    public async Task IsTranscodingDisabledAsync_ReturnsFalse_WhenEnabled()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("EnableTranscoding", "true")).ReturnsAsync("true");
        var service = CreateService();

        // Act
        var result = await service.IsTranscodingDisabledAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsTranscodingDisabledAsync_ReturnsTrue_WhenDisabled()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("EnableTranscoding", "true")).ReturnsAsync("false");
        var service = CreateService();

        // Act
        var result = await service.IsTranscodingDisabledAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsTranscodingDisabledAsync_ReturnsFalse_OnInvalidValue()
    {
        // Arrange
        // If value is "invalid", logic defaults to TRUE (enabled) -> Disabled=False.
        _settingsServiceMock.Setup(s => s.GetSettingAsync("EnableTranscoding", "true")).ReturnsAsync("invalid");
        var service = CreateService();

        // Act
        var result = await service.IsTranscodingDisabledAsync();

        // Assert
        // Expect False (Transcoding is NOT disabled, i.e. Enabled)
        Assert.False(result);
    }

    #endregion

    // GetTranscodeArguments tests are removed as they verify logic now residing in TranscodeProfileBuilder.
    // That logic is covered by SoftMedia.Tests.Services.TranscodingIntegrationTests.
}
