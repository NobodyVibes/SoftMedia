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

    // --- R-WI-003: remux must not bypass the bitrate cap ---

    // h264/aac in mkv → codec-compatible but container needs remux. Client supports the codecs but
    // only the mp4 container, so DirectPlay is out and Remux is the natural choice — unless the
    // source bitrate exceeds the cap, in which case a copy would blow it and Transcode must win.
    private static MediaItem RemuxItem() => new()
    {
        Id = Guid.NewGuid(), Title = "R", Path = "/r.mkv",
        VideoCodec = "h264", AudioCodec = "aac", Container = "mkv", Resolution = "1080p",
    };

    private static StreamPlanService BuildRemuxService(int wanKbps, long sourceBitrateBps,
        string videoCodec = "h264", string audioCodec = "aac")
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = videoCodec, AudioCodec = audioCodec, Resolution = "1080p",
            PixelFormat = "yuv420p", Duration = 100, Bitrate = sourceBitrateBps,
        });
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(wanKbps);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");
        return new StreamPlanService(ffmpeg.Object, settings.Object, NullLogger<StreamPlanService>.Instance);
    }

    [Fact]
    public async Task Remux_Chosen_WhenSourceBitrateWithinCap()
    {
        var svc = BuildRemuxService(wanKbps: 8000, sourceBitrateBps: 3_000_000); // 3 Mbps ≤ 8 Mbps cap
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), RemuxItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(PlaybackMethod.Remux, plan.Method);
    }

    [Fact]
    public async Task Transcode_NotRemux_WhenSourceBitrateExceedsCap()
    {
        // 20 Mbps source, 5 Mbps cap: a stream-copy would bypass the cap, so the planner must
        // transcode (which applies -maxrate) instead of remuxing (R-WI-003 diff-review).
        var svc = BuildRemuxService(wanKbps: 5000, sourceBitrateBps: 20_000_000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), RemuxItem(), Caps(20000), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
    }

    [Fact]
    public async Task Remux_Chosen_WhenNoCap_EvenForHighBitrateSource()
    {
        var svc = BuildRemuxService(wanKbps: 0, sourceBitrateBps: 20_000_000); // no cap
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), RemuxItem(), Caps(0), "tok",
            clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(PlaybackMethod.Remux, plan.Method);
    }

    // --- R-WI-004: audio ladder decision (on a forced-transcode source) ---

    private static StreamPlanService BuildForcedTranscodeAudioService(string audioCodec, int audioChannels, int wanKbps = 0)
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "mpeg2", // forces Transcode regardless of audio
            AudioCodec = audioCodec, AudioChannels = audioChannels,
            Resolution = "1080p", PixelFormat = "yuv420p", Duration = 100,
        });
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(wanKbps);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");
        return new StreamPlanService(ffmpeg.Object, settings.Object, NullLogger<StreamPlanService>.Instance);
    }

    private static MediaItem AudioItem(string audioCodec) => new()
    {
        Id = Guid.NewGuid(), Title = "A", Path = "/a.mkv",
        VideoCodec = "mpeg2", AudioCodec = audioCodec, Container = "mkv", Resolution = "1080p",
    };

    private static ClientCapabilities SurroundCaps() => new()
    {
        VideoCodecs = ["h264"], AudioCodecs = ["aac", "ac3"], SupportedContainers = ["mp4"],
        MaxBitrate = 0, MaxAudioChannels = 6, MaxResolution = 2160,
    };

    [Fact]
    public async Task Audio_Copies_Ac3_5_1_WhenClientSupportsAc3()
    {
        // AC3 5.1 source + AC3-capable client → copy (surround preserved with no re-encode).
        var svc = BuildForcedTranscodeAudioService("ac3", 6);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), AudioItem("ac3"), SurroundCaps(), "tok", clientIp: IPAddress.Parse("10.0.0.1"));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method); // video still transcodes (mpeg2)
        Assert.True(plan.TranscodeAudioCopy);
        Assert.Equal("ac3", plan.TranscodeAudioCodec);
        Assert.Equal(6, plan.TranscodeAudioChannels);
    }

    [Fact]
    public async Task Audio_EncodesInsteadOfCopy_WhenBitrateCapped()
    {
        // AC3 5.1 copyable source, but a bitrate cap is in effect (WAN 8000) → the plan must ENCODE
        // (bounded ≤448k) rather than copy the source audio at its uncapped original bitrate
        // (diff-review MEDIUM). Surround is still preserved as AC3 5.1.
        var svc = BuildForcedTranscodeAudioService("ac3", 6, wanKbps: 8000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), AudioItem("ac3"), SurroundCaps(), "tok", clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.False(plan.TranscodeAudioCopy); // capped → encode, not copy
        Assert.Equal("ac3", plan.TranscodeAudioCodec);
        Assert.Equal(6, plan.TranscodeAudioChannels);
    }

    [Fact]
    public async Task Audio_EncodesAc3_5_1_ForMultichannelSource_ClientCannotCopy()
    {
        // DTS 5.1 can't be copied (not fMP4/TS-safe) but the client wants surround + supports AC3
        // → encode AC3 5.1 rather than downmix to stereo.
        var svc = BuildForcedTranscodeAudioService("dts", 6);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), AudioItem("dts"), SurroundCaps(), "tok", clientIp: IPAddress.Parse("10.0.0.1"));

        Assert.False(plan.TranscodeAudioCopy);
        Assert.Equal("ac3", plan.TranscodeAudioCodec);
        Assert.Equal(6, plan.TranscodeAudioChannels);
    }

    [Fact]
    public async Task Audio_StereoAac_ForStereoSource_NonMuxableCodec()
    {
        // Vorbis stereo: can't copy (not TS/fMP4-safe), not multichannel → stereo AAC.
        var svc = BuildForcedTranscodeAudioService("vorbis", 2);
        var caps = new ClientCapabilities
        {
            VideoCodecs = ["h264"], AudioCodecs = ["aac", "vorbis"], SupportedContainers = ["mp4"],
            MaxBitrate = 0, MaxAudioChannels = 6, MaxResolution = 2160,
        };
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), AudioItem("vorbis"), caps, "tok", clientIp: IPAddress.Parse("10.0.0.1"));

        Assert.False(plan.TranscodeAudioCopy);
        Assert.Equal("aac", plan.TranscodeAudioCodec);
        Assert.Equal(2, plan.TranscodeAudioChannels);
    }

    [Fact]
    public async Task Transcode_NotRemux_WhenAudioCodecNotFmp4Muxable()
    {
        // Vorbis direct-plays in webm/mkv, but ffmpeg's fMP4 muxer has no tag for it — a stream-copy
        // into fMP4-HLS would fail at mux time. The planner must transcode, not remux (R-WI-003 review).
        var svc = BuildRemuxService(wanKbps: 0, sourceBitrateBps: 3_000_000, audioCodec: "vorbis");
        var item = RemuxItem();
        item.AudioCodec = "vorbis";
        var caps = new ClientCapabilities
        {
            VideoCodecs = ["h264"],
            AudioCodecs = ["vorbis"], // client CAN decode vorbis — but it's still not fMP4-remuxable
            SupportedContainers = ["mp4"],
            MaxBitrate = 20000,
            MaxAudioChannels = 2,
            MaxResolution = 2160,
        };

        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), item, caps, "tok", clientIp: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
    }
}
