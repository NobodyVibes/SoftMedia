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
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<ILogger<FFmpegService>> _loggerMock;
    private readonly string _testInputPath = "C:\\test\\input.mkv";
    private readonly string _testOutputDir = "C:\\test\\output";
    private readonly string _testSegmentPrefix = "segment";

    public FFmpegServiceTests()
    {
        _processRunnerMock = new Mock<IProcessRunner>();
        _settingsServiceMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<FFmpegService>>();
        
        // Setup default settings
        SetupDefaultSettings();
    }

    private void SetupDefaultSettings()
    {
        _settingsServiceMock.Setup(s => s.GetSettingAsync("DisableTranscoding", "false")).ReturnsAsync("false");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("none");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodePreset", "veryfast")).ReturnsAsync("veryfast");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeThreadCount", "0")).ReturnsAsync("0");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("original");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeCRF", "23")).ReturnsAsync("23");
    }

    private FFmpegService CreateService()
    {
        return new FFmpegService(_loggerMock.Object, _processRunnerMock.Object, _settingsServiceMock.Object);
    }

    #region ProbeMediaAsync Tests

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

        var service = CreateService();

        // Act
        var result = await service.ProbeMediaAsync("test.mkv");

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

        var service = CreateService();

        // Act
        var result = await service.ProbeMediaAsync("test.mkv");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region IsTranscodingDisabledAsync Tests

    [Fact]
    public async Task IsTranscodingDisabledAsync_ReturnsFalse_WhenDefault()
    {
        // Arrange - default is false
        var service = CreateService();

        // Act
        var result = await service.IsTranscodingDisabledAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsTranscodingDisabledAsync_ReturnsTrue_WhenEnabled()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("DisableTranscoding", "false")).ReturnsAsync("true");
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
        _settingsServiceMock.Setup(s => s.GetSettingAsync("DisableTranscoding", "false")).ReturnsAsync("invalid");
        var service = CreateService();

        // Act
        var result = await service.IsTranscodingDisabledAsync();

        // Assert
        Assert.False(result);
    }

    #endregion

    #region GetTranscodeArguments Default Settings Tests

    [Fact]
    public void GetTranscodeArguments_WithDefaultSettings_UsesLibx264()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v libx264", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithDefaultSettings_UsesVeryFastPreset()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-preset veryfast", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithDefaultSettings_UsesCRF23()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-crf 23", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithDefaultSettings_NoScaleFilter()
    {
        // Arrange - original resolution means no scaling
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.DoesNotContain("scale=", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithDefaultSettings_NoThreadsArg()
    {
        // Arrange - 0 threads means omit the argument (auto)
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.DoesNotContain("-threads", processInfo.Arguments);
    }

    #endregion

    #region GetTranscodeArguments Custom Settings Tests

    [Fact]
    public void GetTranscodeArguments_WithSlowPreset_UsesSlowPreset()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodePreset", "veryfast")).ReturnsAsync("slow");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-preset slow", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithCustomCRF_UsesCustomCRF()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeCRF", "23")).ReturnsAsync("18");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-crf 18", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithCustomThreadCount_UsesThreadCount()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeThreadCount", "0")).ReturnsAsync("4");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-threads 4", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithZeroThreads_OmitsThreadsArg()
    {
        // Arrange - 0 means auto/omit
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeThreadCount", "0")).ReturnsAsync("0");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.DoesNotContain("-threads", processInfo.Arguments);
    }

    #endregion

    #region Hardware Acceleration Tests

    [Fact]
    public void GetTranscodeArguments_WithNvidiaHwAccel_UsesNvenc()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("nvidia");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v h264_nvenc", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithAmdHwAccel_UsesAmf()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("amd");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v h264_amf", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithIntelHwAccel_UsesQsv()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("intel");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v h264_qsv", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithNoHwAccel_UsesLibx264()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("none");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v libx264", processInfo.Arguments);
    }

    #endregion

    #region Resolution Scaling Tests

    [Fact]
    public void GetTranscodeArguments_With720pResolution_AddsScaleFilter()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("720p");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - 720p uses scale=1280:-2
        Assert.Contains("1280", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_With1080pResolution_AddsScaleFilter()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("1080p");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - 1080p uses scale=1920:-2
        Assert.Contains("1920", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_With4kResolution_AddsScaleFilter()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("4k");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - 4k uses scale=3840:-2
        Assert.Contains("3840", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithOriginalResolution_NoScaleFilter()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("original");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.DoesNotContain("scale=", processInfo.Arguments);
    }

    #endregion

    #region Seek and Subtitle Tests

    [Fact]
    public void GetTranscodeArguments_WithSeekPosition_AddsSeekArg()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix, null, 60.5);

        // Assert
        Assert.Contains("-ss 60.50", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_WithSubtitleTrack_AddsOverlayFilter()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix, 2, null);

        // Assert
        Assert.Contains("overlay", processInfo.Arguments);
        Assert.Contains("[0:2]", processInfo.Arguments);
    }

    #endregion

    #region Combined Settings Tests

    [Fact]
    public void GetTranscodeArguments_WithAllCustomSettings_AppliesAll()
    {
        // Arrange - set all custom values
        _settingsServiceMock.Setup(s => s.GetSettingAsync("HardwareAcceleration", "none")).ReturnsAsync("nvidia");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodePreset", "veryfast")).ReturnsAsync("medium");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("TranscodeThreadCount", "0")).ReturnsAsync("8");
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original")).ReturnsAsync("1080p");
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v h264_nvenc", processInfo.Arguments);
        Assert.Contains("-preset p4", processInfo.Arguments); // NVENC translates "medium" to "p4"
        Assert.Contains("-threads 8", processInfo.Arguments);
        Assert.Contains("1920", processInfo.Arguments); // 1080p uses width 1920
    }

    [Fact]
    public void GetTranscodeArguments_AlwaysIncludesHlsOutput()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-f hls", processInfo.Arguments);
        Assert.Contains("-hls_time 6", processInfo.Arguments);
        Assert.Contains("master.m3u8", processInfo.Arguments);
    }

    [Fact]
    public void GetTranscodeArguments_AlwaysIncludesAudioEncoding()
    {
        // Arrange
        var service = CreateService();

        // Act
        var processInfo = service.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:a aac", processInfo.Arguments);
        Assert.Contains("-b:a 128k", processInfo.Arguments);
    }

    #endregion
}
