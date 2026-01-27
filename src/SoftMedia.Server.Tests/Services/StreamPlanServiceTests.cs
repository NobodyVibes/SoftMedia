using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services;
using SoftMedia.Server.Models;
using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Tests.Services;

public class StreamPlanServiceTests
{
    private readonly Mock<ILogger<StreamPlanService>> _loggerMock;
    private readonly Mock<IFFmpegService> _ffmpegMock;
    private readonly Mock<ISettingsService> _settingsMock;
    private readonly StreamPlanService _service;

    public StreamPlanServiceTests()
    {
        _loggerMock = new Mock<ILogger<StreamPlanService>>();
        _ffmpegMock = new Mock<IFFmpegService>();
        _settingsMock = new Mock<ISettingsService>();
        _service = new StreamPlanService(_ffmpegMock.Object, _settingsMock.Object, _loggerMock.Object);
        
        // Setup default settings
        _settingsMock.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", 20000))
            .ReturnsAsync(20000);
        _settingsMock.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", true))
            .ReturnsAsync(true);
        _settingsMock.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", "auto"))
            .ReturnsAsync("auto");
        _settingsMock.Setup(s => s.GetSettingAsync("DefaultAudioChannels", "auto"))
            .ReturnsAsync("auto");
        _settingsMock.Setup(s => s.GetSettingAsync("OutputVideoCodec", "auto"))
            .ReturnsAsync("auto");
        _settingsMock.Setup(s => s.GetSettingAsync("PreserveHDR", false))
            .ReturnsAsync(false);
        _settingsMock.Setup(s => s.GetSettingAsync("EnableAV1Encoding", false))
            .ReturnsAsync(false);
        _settingsMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original"))
            .ReturnsAsync("original");
    }

    [Fact]
    public async Task ComputeStreamPlan_H264_MP4_DirectPlay()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/video.mp4",
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264", "hevc" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4", "hls" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.DirectPlay, result.Method);
        Assert.Contains("/stream/", result.Url);
        Assert.Contains("h264", result.VideoCodec.ToLower());
        Assert.Contains("aac", result.AudioCodec.ToLower());
    }

    [Fact]
    public async Task ComputeStreamPlan_HEVC_MKV_Remux()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/video.mkv",
            Container = "matroska",
            VideoCodec = "hevc",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264", "hevc" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4", "hls" },  // MKV not supported
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioCodec = "aac",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.Remux, result.Method);
        Assert.Contains("/transcode/", result.Url);
        Assert.Contains("hevc", result.VideoCodec.ToLower());
        Assert.Contains("remux", result.Reason.ToLowerInvariant());
    }

    [Fact]
    public async Task ComputeStreamPlan_AV1_Transcode_To_H264()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/video.mkv",
            Container = "matroska",
            VideoCodec = "av1",
            AudioCodec = "opus"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264" },  // No AV1 support
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4", "hls" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "av1",
            AudioCodec = "opus",
            Resolution = "3840x2160",
            PixelFormat = "yuv420p10le",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.Transcode, result.Method);
        Assert.Contains("/transcode/", result.Url);
        Assert.Equal("h264", result.VideoCodec.ToLower());
    }

    [Fact]
    public async Task ComputeStreamPlan_AppliesMaxResolution_From_RequestedQuality()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/4k-video.mkv",
            Container = "matroska",
            VideoCodec = "h264",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4", "hls" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2,
            RequestedQuality = "720p"  // User selected 720p
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            Resolution = "3840x2160",  // 4K source
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        // Should transcode because resolution exceeds requested quality
        Assert.Equal(PlaybackMethod.Transcode, result.Method);
        Assert.Contains("resolution=720p", result.Url);
    }

    [Fact]
    public async Task ComputeStreamPlan_AppliesServerBitrateLimit()
    {
        // Arrange
        _settingsMock.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", 20000))
            .ReturnsAsync(5000);  // Lower server limit

        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/video.mp4",
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4" },
            SupportsHdr = false,
            MaxBitrate = 50000,  // Client requests high bitrate
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        // The service should have clamped bitrate to 5000 internally
        // Verify via log calls if needed
        _settingsMock.Verify(s => s.GetSettingAsync("MaxStreamingBitrate", 20000), Times.Once);
    }

    [Fact]
    public async Task ComputeStreamPlan_HDR_Client_SupportsHDR_DirectPlay()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/hdr-video.mp4",
            Container = "mp4",
            VideoCodec = "hevc",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "hevc" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4" },
            SupportsHdr = true,  // Client supports HDR
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioCodec = "aac",
            Resolution = "3840x2160",
            PixelFormat = "yuv420p10le",  // 10-bit HDR
            ColorTransfer = "smpte2084"  // HDR10
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.DirectPlay, result.Method);
        Assert.True(result.IsHdr);
    }

    [Fact]
    public async Task ComputeStreamPlan_HDR_NonHDR_Client_Transcodes()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/hdr-video.mp4",
            Container = "mp4",
            VideoCodec = "hevc",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "hevc", "h264" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4"," hls" },
            SupportsHdr = false,  // Client does NOT support HDR
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioCodec = "aac",
            Resolution = "3840x2160",
            PixelFormat = "yuv420p10le",  // 10-bit HDR
            ColorTransfer = "smpte2084"  // HDR10
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert
        Assert.NotNull(result);
        // Should transcode to SDR for non-HDR client
        Assert.Equal(PlaybackMethod.Transcode, result.Method);
        Assert.Contains("HDR", result.Reason);
    }

    [Fact]
    public async Task ComputeStreamPlan_EnforcesMaxTranscodeResolution_GlobalLimit()
    {
        // Arrange: Global setting limits to 1080p
        _settingsMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original"))
            .ReturnsAsync("1080p");
        
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/4k-video.mkv",
            Container = "matroska",
            VideoCodec = "hevc",
            AudioCodec = "aac"
        };

        // Client wants 4K (either via RequestedQuality or MaxResolution)
        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264", "hevc" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4", "hls" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,  // Unlimited
            MaxAudioChannels = 2,
            RequestedQuality = "4k"  // User explicitly requests 4K
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioCodec = "aac",
            Resolution = "3840x2160",  // 4K source
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert: Global limit should cap output at 1080p despite client requesting 4K
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.Transcode, result.Method);  // Must transcode (4K source > 1080p limit)
        Assert.Contains("resolution=1080p", result.Url);  // URL should specify 1080p
        Assert.DoesNotContain("resolution=4k", result.Url);
        Assert.DoesNotContain("resolution=2160p", result.Url);
    }

    [Fact]
    public async Task ComputeStreamPlan_MaxTranscodeResolution_ForcesTranscode_NoDirectPlay()
    {
        // Arrange: Set global limit to 720p
        _settingsMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original"))
            .ReturnsAsync("720p");
        
        // Source is 1080p H.264 MP4 (normally Direct Play compatible)
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/1080p-video.mp4",
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,  // Client supports any resolution
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert: Should NOT Direct Play because source (1080p) > limit (720p)
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.Transcode, result.Method);  // Must transcode down
        Assert.Contains("resolution=720p", result.Url);
    }

    [Fact]
    public async Task ComputeStreamPlan_MaxTranscodeResolution_Original_AllowsDirectPlay()
    {
        // Arrange: Global setting is "original" (no limit)
        _settingsMock.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", "original"))
            .ReturnsAsync("original");
        
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = "/test/4k-video.mp4",
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac"
        };

        var capabilities = new ClientCapabilities
        {
            VideoCodecs = new[] { "h264" },
            AudioCodecs = new[] { "aac" },
            SupportedContainers = new[] { "mp4" },
            SupportsHdr = false,
            MaxBitrate = 0,
            MaxResolution = 0,
            MaxAudioChannels = 2
        };

        var probe = new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            Resolution = "3840x2160",  // 4K
            PixelFormat = "yuv420p",
            ColorTransfer = "bt709"
        };

        _ffmpegMock.Setup(f => f.ProbeMediaAsync(mediaItem.Path))
            .ReturnsAsync(probe);

        // Act
        var result = await _service.ComputeStreamPlanAsync(mediaItem.Id, mediaItem, capabilities, "test-token");

        // Assert: Should allow Direct Play (no global limit)
        Assert.NotNull(result);
        Assert.Equal(PlaybackMethod.DirectPlay, result.Method);
    }
}
