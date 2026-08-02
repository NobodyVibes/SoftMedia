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

/// QS-WI-010 — the arbitration MATRIX: factor COMBINATIONS (client ask × session
/// override × Data Saver × user caps incl. the above-WAN override × LAN/WAN ×
/// resolution ceilings), each asserting BOTH the delivered plan facts (method,
/// TranscodeMaxBitrate, Resolution) AND the emitted winner code — plus the absence of
/// the losing codes, so a wrong winner can't hide behind a right value. Single-factor
/// paths live in StreamPlanServiceCapArbitrationTests; these are the interactions.
public class StreamPlanServiceArbitrationMatrixTests
{
    private static readonly IPAddress LanIp = IPAddress.Parse("192.168.1.50");
    private static readonly IPAddress WanIp = IPAddress.Parse("203.0.113.9");

    /// mpeg2/mp3-in-mkv 4K source: neither direct-playable nor remuxable → Transcode.
    private static MediaItem ForcedTranscodeItem() => new()
    {
        Id = Guid.NewGuid(), Title = "T", Path = "/x.mkv",
        VideoCodec = "mpeg2", AudioCodec = "mp3", Container = "mkv", Resolution = "3840x2160",
    };

    private static StreamPlanService BuildService(
        int wanKbps = 0, int lanKbps = 0,
        string remoteMaxResolution = "original", string maxTranscodeResolution = "original")
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "mpeg2", AudioCodec = "mp3", Resolution = "3840x2160",
            PixelFormat = "yuv420p", Duration = 100,
        });

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(wanKbps);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(lanKbps);
        settings.Setup(s => s.GetSettingAsync("RemoteMaxResolution", It.IsAny<string>())).ReturnsAsync(remoteMaxResolution);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync(maxTranscodeResolution);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);

        return new StreamPlanService(ffmpeg.Object, settings.Object,
            new Mock<IOpenClToneMapProbe>().Object, NullLogger<StreamPlanService>.Instance);
    }

    private static ClientCapabilities Caps(int ask = 0, string? quality = null, bool dataSaver = false) => new()
    {
        VideoCodecs = ["h264"], AudioCodecs = ["aac"], SupportedContainers = ["mp4"],
        MaxBitrate = ask, MaxAudioChannels = 2, MaxResolution = 2160,
        RequestedQuality = quality, DataSaver = dataSaver,
    };

    private static void AssertOnlyBitrateCode(StreamPlan plan, string winner, string kbps)
    {
        var code = Assert.Single(plan.ReasonCodes, c => c.Code.StartsWith("bitrate.", StringComparison.Ordinal));
        Assert.Equal(winner, code.Code);
        Assert.Equal(kbps, code.Params["kbps"]);
    }

    private static void AssertNoBitrateCode(StreamPlan plan)
        => Assert.DoesNotContain(plan.ReasonCodes, c => c.Code.StartsWith("bitrate.", StringComparison.Ordinal));

    // ---------- bitrate: ask × tier × user policy ----------

    [Fact]
    public async Task Wan_AskAboveWanCap_WanCapWins()
    {
        var plan = await BuildService(wanKbps: 10000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 20000), "tok", WanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(10000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.WanBitrateCap, "10000");
    }

    [Fact]
    public async Task Wan_UserCapAboveWanCap_UserCapReplacesTheTier()
    {
        // Override-wins (§0/§2): a 30 Mbps account limit REPLACES the 10 Mbps WAN tier.
        var plan = await BuildService(wanKbps: 10000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 40000), "tok", WanIp,
            new UserStreamingPolicy(30000, null, null));

        Assert.Equal(30000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.UserBitrateCap, "30000");
    }

    [Fact]
    public async Task Wan_RemoteVariantBeatsBaseCap_OffLan()
    {
        var plan = await BuildService(wanKbps: 20000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 20000), "tok", WanIp,
            new UserStreamingPolicy(3000, 8000, null));

        Assert.Equal(8000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.UserRemoteBitrateCap, "8000");
    }

    [Fact]
    public async Task Lan_RemoteVariantIgnored_BaseCapApplies()
    {
        var plan = await BuildService(lanKbps: 40000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 20000), "tok", LanIp,
            new UserStreamingPolicy(3000, 8000, null));

        Assert.Equal(3000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.UserBitrateCap, "3000");
    }

    [Fact]
    public async Task Lan_NoLanCapNoUserCap_AskSurvivesUnclamped()
    {
        var plan = await BuildService(wanKbps: 10000, lanKbps: 0).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 20000), "tok", LanIp);

        Assert.Equal(20000, plan.TranscodeMaxBitrate);
        AssertNoBitrateCode(plan);
    }

    // ---------- Data Saver × server caps ----------

    [Fact]
    public async Task DataSaver_BelowEveryServerCap_DataSaverIsTheNamedWinner()
    {
        var plan = await BuildService(wanKbps: 10000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 2000, dataSaver: true), "tok", WanIp);

        Assert.Equal(2000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.DataSaverBitrateCap, "2000");
    }

    [Fact]
    public async Task DataSaver_ServerCapBitesBelowIt_ServerCapNamed_NotDataSaver()
    {
        var plan = await BuildService(wanKbps: 1000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 2000, dataSaver: true), "tok", WanIp);

        Assert.Equal(1000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.WanBitrateCap, "1000");
    }

    [Fact]
    public async Task DataSaver_SurvivesAGenerousUserOverride_DataSaverNamed()
    {
        // The account allows 30 Mbps; the device's own Data Saver ask (2 Mbps) is the
        // binding constraint and must be blamed — not the (unclamping) user policy.
        var plan = await BuildService(wanKbps: 10000).ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 2000, dataSaver: true), "tok", WanIp,
            new UserStreamingPolicy(30000, null, null));

        Assert.Equal(2000, plan.TranscodeMaxBitrate);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.DataSaverBitrateCap, "2000");
    }

    // ---------- resolution: session pick × user/remote/server ceilings ----------

    [Fact]
    public async Task SessionPickBelowUserCeiling_PickWins_AndIsNamed()
    {
        var plan = await BuildService().ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(quality: "720p"), "tok", LanIp,
            new UserStreamingPolicy(null, null, 1080));

        Assert.Equal("720p", plan.Resolution);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code.StartsWith("resolution.", StringComparison.Ordinal)
            || c.Code == StreamReasonCodes.SessionQualityOverride);
        Assert.Equal(StreamReasonCodes.SessionQualityOverride, code.Code);
    }

    [Fact]
    public async Task UserResolutionOverridesRemoteCeiling_OffLan()
    {
        // Resolution override-wins: the account's 2160 beats the server's remote 1080p.
        var plan = await BuildService(remoteMaxResolution: "1080p").ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", WanIp,
            new UserStreamingPolicy(null, null, 2160));

        Assert.Equal("2160p", plan.Resolution);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    [Fact]
    public async Task ServerCeilingClampsBelowRemoteCeiling_ServerNamed()
    {
        var plan = await BuildService(remoteMaxResolution: "1080p", maxTranscodeResolution: "720p")
            .ComputeStreamPlanAsync(Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", WanIp);

        Assert.Equal("720p", plan.Resolution);
        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.ServerResolutionCeiling);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    [Fact]
    public async Task RemoteCeilingDoesNotApplyOnLan()
    {
        var plan = await BuildService(remoteMaxResolution: "1080p").ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", LanIp);

        Assert.Equal("2160p", plan.Resolution);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    // ---------- cross-dimension: bitrate winner + resolution winner together ----------

    [Fact]
    public async Task AboveWanUserCap_And_RemoteResolutionCeiling_BothNamedTogether()
    {
        // The override-wins bitrate cap and the (not-overridden) remote resolution
        // ceiling bind independently — the explainer must name BOTH winners.
        var plan = await BuildService(wanKbps: 10000, remoteMaxResolution: "1080p")
            .ComputeStreamPlanAsync(
                Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 40000), "tok", WanIp,
                new UserStreamingPolicy(30000, null, null));

        Assert.Equal(30000, plan.TranscodeMaxBitrate);
        Assert.Equal("1080p", plan.Resolution);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.UserBitrateCap, "30000");
        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    [Fact]
    public async Task DataSaver_And_SessionQualityPick_BothNamedTogether()
    {
        var plan = await BuildService().ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(ask: 2000, quality: "720p", dataSaver: true),
            "tok", LanIp);

        Assert.Equal(2000, plan.TranscodeMaxBitrate);
        Assert.Equal("720p", plan.Resolution);
        AssertOnlyBitrateCode(plan, StreamReasonCodes.DataSaverBitrateCap, "2000");
        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.SessionQualityOverride);
    }
}
