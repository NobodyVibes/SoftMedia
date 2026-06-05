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

/// CC-WI-002 — the Chromecast-tuned plan. The client sends the Default-Media-Receiver
/// capability profile (H.264 + AAC, ≤1080p, HLS/MP4; see CHROMECAST_CAPABILITIES in
/// VideoPlayer.tsx). These pin the resulting plan to something every Cast generation can
/// decode — transcoding only what must be transcoded, direct-playing what's already
/// compatible — and confirm the per-network/per-user bitrate cap still applies to a cast.
public class StreamPlanServiceCastTests
{
    /// Mirror of the client CHROMECAST_CAPABILITIES profile.
    private static ClientCapabilities CastCaps(int maxBitrate = 0) => new()
    {
        VideoCodecs = ["h264"],
        AudioCodecs = ["aac"],
        SupportedContainers = ["hls", "mp4"],
        MaxResolution = 1080,
        MaxBitrate = maxBitrate,
        MaxAudioChannels = 2,
        RequestedQuality = "1080p",
    };

    private static MediaItem Source(string videoCodec, string container = "mkv", string resolution = "2160p", string audioCodec = "eac3") => new()
    {
        Id = Guid.NewGuid(),
        Title = "T",
        Path = "/x." + container,
        VideoCodec = videoCodec,
        AudioCodec = audioCodec,
        Container = container,
        Resolution = resolution,
    };

    private static StreamPlanService BuildService(string sourceCodec, string sourceResolution = "2160p", string sourceAudioCodec = "eac3", int wanKbps = 0, int lanKbps = 0)
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = sourceCodec,
            AudioCodec = sourceAudioCodec,
            Resolution = sourceResolution,
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

    [Theory]
    [InlineData("hevc")] // the common 4K source
    [InlineData("av1")]  // what this project's own QA file transcoded to
    [InlineData("vp9")]
    public async Task CastCaps_UnsupportedSource_TranscodesToH264Hls1080p(string sourceCodec)
    {
        var svc = BuildService(sourceCodec, sourceResolution: "2160p");

        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), Source(sourceCodec), CastCaps(), "tok",
            clientIp: IPAddress.Parse("192.168.1.5"));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal("h264", plan.VideoCodec);     // never hevc/av1 — the DMR can't decode those
        Assert.Equal("aac", plan.AudioCodec);
        Assert.Equal("hls", plan.Container);
        Assert.Equal("1080p", plan.Resolution);    // 4K source capped to 1080p
        // The receiver fetches this exact URL — assert the params, anchored so a future
        // "codec=h264_nvenc"-style value couldn't satisfy a loose substring.
        Assert.Matches(@"[?&]codec=h264(&|$)", plan.Url);
        Assert.Matches(@"[?&]resolution=1080p(&|$)", plan.Url);
    }

    [Fact]
    public async Task CastCaps_CompatibleSource_DirectPlays_NotNeedlesslyTranscoded()
    {
        // H.264 + AAC in MP4 at 1080p is exactly what the Default Media Receiver plays natively —
        // forcing a transcode here would waste CPU. This is the most important positive path.
        var svc = BuildService("h264", sourceResolution: "1080p", sourceAudioCodec: "aac");

        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), Source("h264", container: "mp4", resolution: "1080p", audioCodec: "aac"),
            CastCaps(), "tok", clientIp: IPAddress.Parse("192.168.1.5"));

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.Equal("h264", plan.VideoCodec);
    }

    [Fact]
    public async Task CastProfile_DrivesDecision_DistinctFromFullyCapableClient()
    {
        // Same HEVC/MKV source: a fully-capable browser direct-plays it; the Chromecast profile
        // must transcode to H.264. Proves the capability profile — not a coincidental default —
        // drives the plan.
        var svc = BuildService("hevc", sourceResolution: "2160p", sourceAudioCodec: "aac");
        var item = Source("hevc", container: "mkv", resolution: "2160p", audioCodec: "aac");

        var fullCaps = new ClientCapabilities
        {
            VideoCodecs = ["h264", "hevc"],
            AudioCodecs = ["aac", "eac3"],
            SupportedContainers = ["mp4", "mkv", "hls"],
            MaxResolution = 2160,
            MaxBitrate = 0,
            MaxAudioChannels = 6,
        };

        var fullPlan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), item, fullCaps, "tok", IPAddress.Parse("192.168.1.5"));
        var castPlan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), item, CastCaps(), "tok", IPAddress.Parse("192.168.1.5"));

        Assert.Equal("hevc", fullPlan.VideoCodec);                    // capable client keeps HEVC...
        Assert.NotEqual(PlaybackMethod.Transcode, fullPlan.Method);   // ...and doesn't transcode
        Assert.Equal("h264", castPlan.VideoCodec);                    // cast profile forces H.264...
        Assert.Equal(PlaybackMethod.Transcode, castPlan.Method);      // ...via transcode
    }

    [Fact]
    public async Task CastCaps_ZeroClientBitrate_StillEnforcesNetworkCap()
    {
        // The receiver requests "unlimited" (MaxBitrate=0); a 10 Mbps WAN cap must still bind,
        // so a cast can't bypass a limit a browser stream would respect.
        var svc = BuildService("hevc", wanKbps: 10000);

        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), Source("hevc"), CastCaps(maxBitrate: 0), "tok",
            clientIp: IPAddress.Parse("203.0.113.9")); // public => WAN

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Matches(@"[?&]bitrate=10000(&|$)", plan.Url);   // cap baked into the cast stream URL
        Assert.Contains("WAN cap", plan.Reason);                // and explained
    }
}
