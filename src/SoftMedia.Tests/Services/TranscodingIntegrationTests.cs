using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Tests.Services;

/// <summary>
/// Integration tests that verify the full flow from database settings to FFmpeg transcoding arguments.
/// These tests use a real in-memory database to ensure settings changes propagate correctly.
/// </summary>
public class TranscodingIntegrationTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly SettingsService _settingsService;
    private readonly Mock<IProcessRunner> _processRunnerMock;
    private readonly Mock<ILogger<FFmpegService>> _ffmpegLoggerMock;
    private readonly Mock<ILogger<SettingsService>> _settingsLoggerMock;
    private readonly string _testInputPath = "C:\\test\\input.mkv";
    private readonly string _testOutputDir = "C:\\test\\output";
    private readonly string _testSegmentPrefix = "segment";

    public TranscodingIntegrationTests()
    {
        // Create in-memory database with unique name per test
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _settingsLoggerMock = new Mock<ILogger<SettingsService>>();
        _settingsService = new SettingsService(_context, _settingsLoggerMock.Object);
        _processRunnerMock = new Mock<IProcessRunner>();
        _ffmpegLoggerMock = new Mock<ILogger<FFmpegService>>();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private FFmpegService CreateFFmpegService()
    {
        return new FFmpegService(_ffmpegLoggerMock.Object, _processRunnerMock.Object, _settingsService);
    }

    /// <summary>
    /// Simulates saving settings via the API (as frontend would do)
    /// </summary>
    private async Task SaveSettingsViaService(List<AppSetting> settings)
    {
        await _settingsService.UpdateSettingsAsync(settings);
    }

    #region Default Settings Integration Tests

    [Fact]
    public async Task Integration_DefaultSettings_ApplyToTranscodeArguments()
    {
        // Arrange - Initialize default settings in database (as app startup would do)
        await _settingsService.InitializeDefaultsAsync();
        var ffmpegService = CreateFFmpegService();

        // Act - Get transcode arguments
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - Default values should be used
        Assert.Contains("-c:v libx264", processInfo.Arguments);  // none hardware = libx264
        Assert.Contains("-preset veryfast", processInfo.Arguments);  // default preset
        Assert.Contains("-crf 23", processInfo.Arguments);  // default CRF
        Assert.DoesNotContain("-threads", processInfo.Arguments);  // 0 = auto, omitted
        Assert.DoesNotContain("scale=", processInfo.Arguments);  // original = no scaling
    }

    [Fact]
    public async Task Integration_DisableTranscoding_DefaultIsFalse()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();
        var ffmpegService = CreateFFmpegService();

        // Act
        var isDisabled = await ffmpegService.IsTranscodingDisabledAsync();

        // Assert
        Assert.False(isDisabled);
    }

    #endregion

    #region Settings Change Integration Tests

    [Fact]
    public async Task Integration_ChangePreset_AffectsNextTranscode()
    {
        // Arrange - Start with defaults
        await _settingsService.InitializeDefaultsAsync();
        var ffmpegService = CreateFFmpegService();

        // First transcode with default preset
        var firstProcessInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);
        Assert.Contains("-preset veryfast", firstProcessInfo.Arguments);

        // Simulate user changing preset via frontend
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodePreset", Value = "slow", Group = "Transcoding" }
        });

        // Act - Create new FFmpegService instance (simulates new request scope)
        var newFfmpegService = CreateFFmpegService();
        var secondProcessInfo = newFfmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - New preset should be used
        Assert.Contains("-preset slow", secondProcessInfo.Arguments);
    }

    [Fact]
    public async Task Integration_ChangeCRF_AffectsNextTranscode()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate user changing CRF from 23 to 18
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodeCRF", Value = "18", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-crf 18", processInfo.Arguments);
        Assert.DoesNotContain("-crf 23", processInfo.Arguments);
    }

    [Fact]
    public async Task Integration_ChangeThreadCount_AffectsNextTranscode()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate user setting thread count to 8
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodeThreadCount", Value = "8", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-threads 8", processInfo.Arguments);
    }

    [Fact]
    public async Task Integration_ChangeResolution_AffectsNextTranscode()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate user setting max resolution to 720p
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "MaxTranscodeResolution", Value = "720p", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - 720p uses scale=1280:-2
        Assert.Contains("scale=1280:-2", processInfo.Arguments);
    }

    [Fact]
    public async Task Integration_ChangeHardwareAccel_AffectsNextTranscode()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate user enabling NVIDIA hardware acceleration
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "nvidia", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert
        Assert.Contains("-c:v h264_nvenc", processInfo.Arguments);
    }

    [Fact]
    public async Task Integration_EnableDisableTranscoding_AffectsCheck()
    {
        // Arrange - Start with defaults (transcoding enabled)
        await _settingsService.InitializeDefaultsAsync();
        var ffmpegService = CreateFFmpegService();
        Assert.False(await ffmpegService.IsTranscodingDisabledAsync());

        // Simulate user disabling transcoding
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "DisableTranscoding", Value = "true", Group = "Transcoding" }
        });

        // Act - New service instance reads updated setting
        var newFfmpegService = CreateFFmpegService();
        var isDisabled = await newFfmpegService.IsTranscodingDisabledAsync();

        // Assert
        Assert.True(isDisabled);
    }

    [Fact]
    public async Task Integration_NvidiaHardwareAccel_IncludesHwaccelCuda()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "nvidia", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - NVIDIA should use CUDA hardware decode + NVENC encode
        Assert.Contains("-hwaccel cuda", processInfo.Arguments);
        Assert.Contains("-hwaccel_output_format cuda", processInfo.Arguments);
        Assert.Contains("-c:v h264_nvenc", processInfo.Arguments);
        
        // Verify hardware decode comes BEFORE -i (required by FFmpeg)
        var hwaccelIndex = processInfo.Arguments.IndexOf("-hwaccel cuda");
        var inputIndex = processInfo.Arguments.IndexOf("-i ");
        Assert.True(hwaccelIndex < inputIndex, "Hardware decode flags must appear before -i input");
    }

    [Fact]
    public async Task Integration_IntelHardwareAccel_IncludesHwaccelQsv()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "intel", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - Intel should use QSV hardware decode + QSV encode
        Assert.Contains("-hwaccel qsv", processInfo.Arguments);
        Assert.Contains("-init_hw_device qsv=hw", processInfo.Arguments);
        Assert.Contains("-c:v h264_qsv", processInfo.Arguments);
        
        // Verify hardware decode comes BEFORE -i
        var hwaccelIndex = processInfo.Arguments.IndexOf("-hwaccel qsv");
        var inputIndex = processInfo.Arguments.IndexOf("-i ");
        Assert.True(hwaccelIndex < inputIndex, "Hardware decode flags must appear before -i input");
    }

    [Fact]
    public async Task Integration_AmdHardwareAccel_IncludesHwaccelD3d11va()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "amd", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - AMD should use D3D11VA hardware decode + AMF encode
        Assert.Contains("-hwaccel d3d11va", processInfo.Arguments);
        Assert.Contains("-c:v h264_amf", processInfo.Arguments);
        
        // Verify hardware decode comes BEFORE -i
        var hwaccelIndex = processInfo.Arguments.IndexOf("-hwaccel d3d11va");
        var inputIndex = processInfo.Arguments.IndexOf("-i ");
        Assert.True(hwaccelIndex < inputIndex, "Hardware decode flags must appear before -i input");
    }

    [Fact]
    public async Task Integration_NoHardwareAccel_NoHwaccelFlags()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "none", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - No hardware acceleration = no hwaccel flags
        Assert.DoesNotContain("-hwaccel", processInfo.Arguments);
        Assert.Contains("-c:v libx264", processInfo.Arguments);
    }

    #endregion

    #region Multiple Settings Change Integration Tests

    [Fact]
    public async Task Integration_ChangeMultipleSettings_AllApplyToNextTranscode()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate user changing multiple settings at once (as frontend Save button would)
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "HardwareAcceleration", Value = "intel", Group = "Transcoding" },
            new() { Key = "TranscodePreset", Value = "medium", Group = "Transcoding" },
            new() { Key = "TranscodeThreadCount", Value = "4", Group = "Transcoding" },
            new() { Key = "MaxTranscodeResolution", Value = "1080p", Group = "Transcoding" },
            new() { Key = "TranscodeCRF", Value = "20", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - All settings should be applied
        Assert.Contains("-c:v h264_qsv", processInfo.Arguments);  // Intel QSV
        Assert.Contains("-preset medium", processInfo.Arguments);  // QSV keeps original preset names
        Assert.Contains("-threads 4", processInfo.Arguments);
        Assert.Contains("scale=1920:-2", processInfo.Arguments);  // 1080p
        Assert.Contains("-global_quality 20", processInfo.Arguments);  // QSV uses global_quality instead of CRF
    }

    [Fact]
    public async Task Integration_RevertToDefaults_WorksCorrectly()
    {
        // Arrange - Set custom settings first
        await _settingsService.InitializeDefaultsAsync();
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodePreset", Value = "slow", Group = "Transcoding" },
            new() { Key = "TranscodeCRF", Value = "15", Group = "Transcoding" }
        });

        // Verify custom settings applied
        var customService = CreateFFmpegService();
        var customArgs = customService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);
        Assert.Contains("-preset slow", customArgs.Arguments);
        Assert.Contains("-crf 15", customArgs.Arguments);

        // Simulate user reverting to defaults
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodePreset", Value = "veryfast", Group = "Transcoding" },
            new() { Key = "TranscodeCRF", Value = "23", Group = "Transcoding" }
        });

        // Act
        var defaultService = CreateFFmpegService();
        var defaultArgs = defaultService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - Back to defaults
        Assert.Contains("-preset veryfast", defaultArgs.Arguments);
        Assert.Contains("-crf 23", defaultArgs.Arguments);
    }

    #endregion

    #region Edge Case Integration Tests

    [Fact]
    public async Task Integration_InvalidSettingValue_FallsBackToDefault()
    {
        // Arrange
        await _settingsService.InitializeDefaultsAsync();

        // Simulate corrupted/invalid setting value
        await SaveSettingsViaService(new List<AppSetting>
        {
            new() { Key = "TranscodeCRF", Value = "not-a-number", Group = "Transcoding" }
        });

        // Act
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - Should use fallback value (23)
        Assert.Contains("-crf 23", processInfo.Arguments);
    }

    [Fact]
    public async Task Integration_MissingSettingKey_UsesDefault()
    {
        // Arrange - Don't initialize defaults, only add some settings
        _context.Settings.Add(new AppSetting { Key = "HardwareAcceleration", Value = "none", Group = "Transcoding" });
        await _context.SaveChangesAsync();

        // Act - Other settings should use defaults
        var ffmpegService = CreateFFmpegService();
        var processInfo = ffmpegService.GetTranscodeArguments(_testInputPath, _testOutputDir, _testSegmentPrefix);

        // Assert - Default values for missing keys
        Assert.Contains("-preset veryfast", processInfo.Arguments);
        Assert.Contains("-crf 23", processInfo.Arguments);
    }

    #endregion
}
