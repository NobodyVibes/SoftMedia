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

/// QS-WI-001..003 â€” the cap arbitration matrix and its reason codes: the per-user remote
/// bitrate variant (off-LAN only), the per-user/remote/server resolution ceilings, the
/// override-wins semantic (a user cap may EXCEED the network tier â€” deliberate), and one
/// structured winner code per clamp path (bitrate.user-cap/.user-remote-cap/.lan-cap/
/// .wan-cap/.data-saver, quality.session-override, resolution.user-/remote-/server-ceiling,
/// source.is-smaller).
public class StreamPlanServiceCapArbitrationTests
{
    private static readonly IPAddress LanIp = IPAddress.Parse("192.168.1.50");
    private static readonly IPAddress WanIp = IPAddress.Parse("203.0.113.9");

    /// mpeg2/mp3-in-mkv is neither direct-playable nor remuxable for a default browser,
    /// so the planner deterministically produces a Transcode plan.
    private static MediaItem ForcedTranscodeItem() => new()
    {
        Id = Guid.NewGuid(), Title = "T", Path = "/x.mkv",
        VideoCodec = "mpeg2", AudioCodec = "mp3", Container = "mkv", Resolution = "3840x2160",
    };

    private static MediaItem DirectPlayableItem() => new()
    {
        Id = Guid.NewGuid(), Title = "DP", Path = "/dp.mp4",
        VideoCodec = "h264", AudioCodec = "aac", Container = "mp4", Resolution = "1920x1080",
    };

    private static StreamPlanService BuildService(
        int wanKbps = 0, int lanKbps = 0,
        string remoteMaxResolution = "original", string maxTranscodeResolution = "original",
        string probeVideoCodec = "mpeg2", string probeAudioCodec = "mp3",
        string probeResolution = "3840x2160", long? probeBitrateBps = null)
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = probeVideoCodec,
            AudioCodec = probeAudioCodec,
            Resolution = probeResolution,
            PixelFormat = "yuv420p",
            Duration = 100,
            Bitrate = probeBitrateBps,
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

        return new StreamPlanService(ffmpeg.Object, settings.Object, new Mock<IOpenClToneMapProbe>().Object, NullLogger<StreamPlanService>.Instance);
    }

    private static ClientCapabilities Caps(int requestedBitrate = 0, string? quality = null, bool dataSaver = false) => new()
    {
        VideoCodecs = ["h264"],
        AudioCodecs = ["aac"],
        SupportedContainers = ["mp4"],
        MaxBitrate = requestedBitrate,
        MaxAudioChannels = 2,
        MaxResolution = 2160,
        RequestedQuality = quality,
        DataSaver = dataSaver,
    };

    // ---------- bitrate winner codes ----------

    [Fact]
    public async Task LanCapClamp_EmitsLanCapCode()
    {
        var svc = BuildService(wanKbps: 0, lanKbps: 5000);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok", LanIp);

        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.LanBitrateCap);
        Assert.Equal("5000", code.Params["kbps"]);
    }

    [Fact]
    public async Task UserCapClamp_EmitsUserCapCode()
    {
        var svc = BuildService(wanKbps: 0, lanKbps: 0);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok", WanIp,
            new UserStreamingPolicy(3000, null, null));

        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserBitrateCap);
        Assert.Equal("3000", code.Params["kbps"]);
    }

    [Fact]
    public async Task UserRemoteCap_AppliesOffLan_AndBeatsBaseCap()
    {
        var svc = BuildService(wanKbps: 20000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok", WanIp,
            new UserStreamingPolicy(MaxBitrateKbps: 3000, RemoteMaxBitrateKbps: 8000, MaxResolution: null));

        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserRemoteBitrateCap);
        Assert.Equal("8000", code.Params["kbps"]);
        Assert.Contains("user remote cap", plan.Reason);
        Assert.Equal(8000, plan.TranscodeMaxBitrate);
    }

    [Fact]
    public async Task UserRemoteCap_IsIgnoredOnLan_BaseCapApplies()
    {
        var svc = BuildService();
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok", LanIp,
            new UserStreamingPolicy(MaxBitrateKbps: 3000, RemoteMaxBitrateKbps: 8000, MaxResolution: null));

        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserBitrateCap);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserRemoteBitrateCap);
        Assert.Equal(3000, plan.TranscodeMaxBitrate);
    }

    [Fact]
    public async Task UserCapAboveWanCap_IsHonored_OverrideWins()
    {
        // Deliberate semantic (Â§0/Â§2): the per-user cap REPLACES the network tier â€” an
        // account limit of 30 Mbps beats a 10 Mbps WAN cap, it is not min'd against it.
        var svc = BuildService(wanKbps: 10000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(40000), "tok", WanIp,
            new UserStreamingPolicy(30000, null, null));

        Assert.Equal(30000, plan.TranscodeMaxBitrate);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserBitrateCap);
        Assert.Equal("30000", code.Params["kbps"]);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.WanBitrateCap);
    }

    [Fact]
    public async Task DataSaverAsk_IsNamedAsTheBindingConstraint()
    {
        // No server cap in play: the client's flagged Data Saver ask is the ceiling.
        var svc = BuildService();
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(2000, dataSaver: true), "tok", LanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.DataSaverBitrateCap);
        Assert.Equal("2000", code.Params["kbps"]);
    }

    [Fact]
    public async Task DataSaver_NotNamed_WhenAServerCapClampsBelowIt()
    {
        // The WAN cap (1000) bites below the Data Saver ask (2000) â€” the server cap is the
        // winner and Data Saver must not be blamed.
        var svc = BuildService(wanKbps: 1000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(2000, dataSaver: true), "tok", WanIp);

        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.DataSaverBitrateCap);
        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.WanBitrateCap);
    }

    // ---------- resolution ceilings (QS-WI-001/002) ----------

    [Fact]
    public async Task UserResolutionCeiling_Clamps_AndIsNamed()
    {
        var svc = BuildService();
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", LanIp,
            new UserStreamingPolicy(null, null, 1080));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal("1080p", plan.Resolution);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserResolutionCeiling);
        Assert.Equal("1080p", code.Params["max"]);
        // The named winner replaces the generic "exceeds your limit" line.
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.ResolutionExceedsMax);
    }

    [Fact]
    public async Task RemoteMaxResolution_Clamps_OffLanOnly()
    {
        var svc = BuildService(remoteMaxResolution: "1080p");

        var remote = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", WanIp);
        Assert.Equal("1080p", remote.Resolution);
        Assert.Single(remote.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);

        var lan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", LanIp);
        Assert.Equal("2160p", lan.Resolution);
        Assert.DoesNotContain(lan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    [Fact]
    public async Task UserResolutionCeiling_OverridesRemoteMaxResolution()
    {
        // Override-wins for resolution too: an account allowed 2160p streams 4K remotely
        // even when the server's remote ceiling is 1080p.
        var svc = BuildService(remoteMaxResolution: "1080p");
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", WanIp,
            new UserStreamingPolicy(null, null, 2160));

        Assert.Equal("2160p", plan.Resolution);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.UserResolutionCeiling);
    }

    [Fact]
    public async Task ServerResolutionCeiling_StillClampsAboveUserOverride_AndIsNamed()
    {
        // MaxTranscodeResolution is the server-wide hardware guardrail, not a network cap â€”
        // it clamps on top of everything (documented in Â§2; the user override only replaces
        // the NETWORK ceiling).
        var svc = BuildService(maxTranscodeResolution: "720p");
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", LanIp,
            new UserStreamingPolicy(null, null, 2160));

        Assert.Equal("720p", plan.Resolution);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.ServerResolutionCeiling);
        Assert.Equal("720p", code.Params["max"]);
    }

    [Fact]
    public async Task SessionQualityPick_IsNamedAsTheWinner()
    {
        var svc = BuildService();
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(quality: "720p"), "tok", LanIp);

        Assert.Equal("720p", plan.Resolution);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.SessionQualityOverride);
        Assert.Equal("720p", code.Params["quality"]);
    }

    // ---------- unified quality-label parsing (QualityLabels, 2026-08-02) ----------

    [Fact]
    public async Task SessionPick1440p_IsHonored_NotSilentlyIgnored()
    {
        // Before unification the plan-side parser didn't know "1440p" (or 480p/8k): the
        // pick fell through to uncapped while other gates enforced it. One authority now.
        var svc = BuildService();
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(quality: "1440p"), "tok", LanIp);

        Assert.Equal("1440p", plan.Resolution);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.SessionQualityOverride);
        Assert.Equal("1440p", code.Params["quality"]);
    }

    [Fact]
    public async Task RemoteCeiling1440p_Clamps_MatchingTheStreamGate()
    {
        // The drift scenario: a hand-set RemoteMaxResolution of "1440p" was enforced by the
        // plan-less /stream gate (ResolutionRank) but uncapped in plan arbitration.
        var svc = BuildService(remoteMaxResolution: "1440p");
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(), "tok", WanIp);

        Assert.Equal("1440p", plan.Resolution);
        Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.RemoteResolutionCeiling);
    }

    // ---------- QS-WI-008: trustworthy Auto (single-rendition reality) ----------

    [Fact]
    public async Task Auto_DirectPlays_WhenSourceIsCompatible()
    {
        // Auto (no session pick, no client bitrate ask) = the SERVER decides: a
        // browser-compatible source direct-plays. No client-side bandwidth guessing exists
        // anywhere in this path — the client sends capabilities, never a measured estimate.
        var svc = BuildService(probeVideoCodec: "h264", probeAudioCodec: "aac", probeResolution: "1920x1080");
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), DirectPlayableItem(), Caps(), "tok", LanIp);

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
    }

    [Fact]
    public async Task Auto_FallsBackToOneTranscodeAtTheEffectiveCap()
    {
        // When neither direct play nor remux is possible, Auto produces exactly ONE
        // transcode bounded by the session's effective cap (here the WAN tier). There is
        // no multi-rendition ABR ladder in this architecture (out of scope by design, §4).
        var svc = BuildService(wanKbps: 8000);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), ForcedTranscodeItem(), Caps(20000), "tok", WanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(8000, plan.TranscodeMaxBitrate);
    }

    [Fact]
    public async Task QualityAboveSource_EmitsSourceIsSmaller_OnDirectPlay()
    {
        // "Why is my 4K pick playing at 1080p?" â€” because the file is 1080p. The code rides
        // on the direct-play plan too, not only on transcodes.
        var svc = BuildService(probeVideoCodec: "h264", probeAudioCodec: "aac", probeResolution: "1920x1080");
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), DirectPlayableItem(), Caps(quality: "4k"), "tok", LanIp);

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        var code = Assert.Single(plan.ReasonCodes, c => c.Code == StreamReasonCodes.SourceIsSmaller);
        Assert.Equal("4k", code.Params["requested"]);
        Assert.Equal("1080p", code.Params["source"]);
    }
}
