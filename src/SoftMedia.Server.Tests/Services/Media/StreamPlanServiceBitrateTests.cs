using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Covers the network-aware bitrate clamping added in P1-WI-003: LAN vs WAN ceiling
/// selection, the per-user override, and the StreamPlan.Reason annotation. The source
/// is forced to an unsupported codec so a Transcode plan is produced deterministically.
public class StreamPlanServiceBitrateTests
{
    private static MediaItem ForcedTranscodeItem() => new()
    {
        Id = Guid.NewGuid(),
        Title = "T",
        Path = "/x.mkv",
        // mpeg2 video in an mkv container is neither direct-play nor remux-able to a
        // default browser, so the planner always falls through to Transcode.
        VideoCodec = "mpeg2",
        AudioCodec = "mp3",
        Container = "mkv",
        Resolution = "1080p",
    };

    private static StreamPlanService BuildService(int wanKbps, int lanKbps)
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "mpeg2",
            AudioCodec = "mp3",
            Resolution = "1080p",
            PixelFormat = "yuv420p",
            Duration = 100,
        });

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(wanKbps);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(lanKbps);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");

        return new StreamPlanService(ffmpeg.Object, settings.Object, NullLogger<StreamPlanService>.Instance);
    }

    private static ClientCapabilities Caps(int requestedBitrate) => new()
    {
        VideoCodecs = ["h264"],
        AudioCodecs = ["aac"],
        SupportedContainers = ["mp4"],
        MaxBitrate = requestedBitrate,
        MaxAudioChannels = 2,
        MaxResolution = 2160,
    };

    [Fact]
    public async Task WanClient_RequestExceedsWanCap_IsClamped_AndReasonAnnotated()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9")); // public => WAN

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains("WAN cap", plan.Reason);
        Assert.Contains("10000", plan.Reason);
    }

    [Fact]
    public async Task LanClient_IsUnaffectedByWanCap_WhenLanUnlimited()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("192.168.1.50")); // LAN, lan cap = unlimited

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.DoesNotContain("cap", plan.Reason);
    }

    [Fact]
    public async Task LanClient_RespectsLanCap_WhenSet()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 5000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("10.0.0.4")); // LAN

        Assert.Contains("LAN cap", plan.Reason);
        Assert.Contains("5000", plan.Reason);
    }

    [Fact]
    public async Task PerUserOverride_TakesPrecedenceOverNetworkCap()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"), userMaxBitrateKbps: 3000);

        Assert.Contains("user policy", plan.Reason);
        Assert.Contains("3000", plan.Reason);
    }

    [Fact]
    public async Task NoClamp_WhenRequestUnderCap_NoBitrateNote()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(4000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.DoesNotContain("cap", plan.Reason);
        Assert.DoesNotContain("Bitrate limited", plan.Reason);
    }

    // --- Structured reason codes (P2-WI-002) ---

    [Fact]
    public async Task TranscodePlan_EmitsStructuredVideoCodecCode()
    {
        var svc = BuildService(wanKbps: 0, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(4000), "tok",
            clientIp: IPAddress.Parse("192.168.1.10"));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        var videoCode = plan.ReasonCodes.FirstOrDefault(c => c.Code == "video.codec.unsupported");
        Assert.NotNull(videoCode);
        Assert.Equal("mpeg2", videoCode!.Params["codec"]);
    }

    [Fact]
    public async Task ClampedTranscode_EmitsStructuredBitrateCode_WithSourceAndKbps()
    {
        var svc = BuildService(wanKbps: 10000, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"));

        var clamp = plan.ReasonCodes.FirstOrDefault(c => c.Code == "bitrate.clamped");
        Assert.NotNull(clamp);
        Assert.Equal("10000", clamp!.Params["kbps"]);
        Assert.Equal("WAN cap", clamp.Params["source"]);
    }
}
