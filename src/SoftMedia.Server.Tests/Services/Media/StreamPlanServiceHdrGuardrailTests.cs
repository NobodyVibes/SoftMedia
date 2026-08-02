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

/// <summary>
/// QS-WI-005/QS-WI-004 — the plan's HDR-guardrail facts and tone-map cause taxonomy.
/// The guardrail matrix (cause × hwaccel none/intel/amd/nvidia) pins that ToneMapPlanned /
/// ToneMapPipeline / ToneMapIsSoftware always mirror the profile builder's single pipeline
/// authority, that WarnOnHdrTranscode/BlockHdrTranscode surface as HdrTranscodePolicy, and
/// that every tone-map names its ACTUAL cause (device, server policy, subtitle burn-in —
/// explicitly — or an 8-bit output codec).
/// </summary>
public class StreamPlanServiceHdrGuardrailTests
{
    /// HDR source that always transcodes: hevc is fine for an hevc-capable client, but the
    /// DTS audio can be neither direct-played nor remuxed, so even HDR-capable clients fall
    /// through to Transcode (letting the tests isolate the HDR causes).
    private static MediaItem HdrItem() => new()
    {
        Id = Guid.NewGuid(),
        Title = "HDR",
        Path = "/hdr.mkv",
        VideoCodec = "hevc",
        AudioCodec = "dts",
        Container = "mkv",
        Resolution = "3840x2160",
    };

    private static StreamPlanService BuildService(
        string hwAccel = "none",
        bool preserveHdr = false,
        bool warnHdr = true,
        bool blockHdr = false,
        bool openClAvailable = true,
        string outputCodec = "auto")
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioCodec = "dts",
            Resolution = "3840x2160",
            PixelFormat = "yuv420p10le",
            ColorTransfer = "smpte2084",
            Duration = 100,
        });

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync(outputCodec);
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(preserveHdr);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");
        settings.Setup(s => s.GetSettingAsync("HardwareAcceleration", It.IsAny<string>())).ReturnsAsync(hwAccel);
        settings.Setup(s => s.GetSettingAsync("WarnOnHdrTranscode", It.IsAny<bool>())).ReturnsAsync(warnHdr);
        settings.Setup(s => s.GetSettingAsync("BlockHdrTranscode", It.IsAny<bool>())).ReturnsAsync(blockHdr);

        var openCl = new Mock<IOpenClToneMapProbe>();
        openCl.Setup(p => p.IsAvailableAsync()).ReturnsAsync(openClAvailable);

        return new StreamPlanService(ffmpeg.Object, settings.Object, openCl.Object,
            NullLogger<StreamPlanService>.Instance);
    }

    /// A client that cannot play HDR (the classic tonemap-for-the-device case).
    private static ClientCapabilities SdrClient() => new()
    {
        VideoCodecs = ["h264"],
        AudioCodecs = ["aac"],
        SupportedContainers = ["mp4"],
        SupportsHdr = false,
        MaxAudioChannels = 2,
    };

    /// An HDR-capable client that can decode hevc (so passthrough is negotiable).
    private static ClientCapabilities HdrClient(int? subtitleTrack = null, bool hevc = true) => new()
    {
        VideoCodecs = hevc ? ["h264", "hevc"] : ["h264"],
        AudioCodecs = ["aac"],
        SupportedContainers = ["mp4"],
        SupportsHdr = true,
        MaxAudioChannels = 2,
        SubtitleTrackIndex = subtitleTrack,
    };

    private static readonly IPAddress LanIp = IPAddress.Parse("192.168.1.50");

    // ---- The guardrail matrix: pipeline per hwaccel (SDR device forces the tone-map) ----

    [Theory]
    [InlineData("none", "software", true, false)]
    [InlineData("intel", "opencl", false, true)]
    [InlineData("amd", "opencl", false, true)]
    [InlineData("nvidia", "cuda", false, true)]
    public async Task ToneMap_pipeline_facts_mirror_the_builder_authority_per_hwaccel(
        string hwAccel, string expectedPipeline, bool expectSoftware, bool expectHwEnabled)
    {
        var svc = BuildService(hwAccel: hwAccel);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), SdrClient(), "tok", LanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.True(plan.ToneMapPlanned);
        Assert.Equal(expectedPipeline, plan.ToneMapPipeline);
        Assert.Equal(expectSoftware, plan.ToneMapIsSoftware);
        Assert.Equal(expectHwEnabled, plan.HardwareAccelerationEnabled);
        Assert.Equal("warn", plan.HdrTranscodePolicy); // WarnOnHdrTranscode default on
        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemap);
    }

    [Theory]
    [InlineData("intel")]
    [InlineData("amd")]
    public async Task Missing_opencl_runtime_reports_the_software_fallback_truthfully(string hwAccel)
    {
        var svc = BuildService(hwAccel: hwAccel, openClAvailable: false);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), SdrClient(), "tok", LanIp);

        Assert.True(plan.ToneMapPlanned);
        Assert.Equal("software", plan.ToneMapPipeline);
        Assert.True(plan.ToneMapIsSoftware);
        Assert.True(plan.HardwareAccelerationEnabled); // hw accel configured, tone-map still software
    }

    // ---- Policy (WarnOnHdrTranscode / BlockHdrTranscode) ----

    [Fact]
    public async Task Block_setting_wins_over_warn()
    {
        var svc = BuildService(blockHdr: true, warnHdr: true);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), SdrClient(), "tok", LanIp);

        Assert.Equal("block", plan.HdrTranscodePolicy);
    }

    [Fact]
    public async Task Both_guardrail_settings_off_leaves_the_facts_but_no_policy()
    {
        var svc = BuildService(warnHdr: false, blockHdr: false);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), SdrClient(), "tok", LanIp);

        Assert.True(plan.ToneMapPlanned); // the facts stay truthful for the debug panel
        Assert.Null(plan.HdrTranscodePolicy); // but no prompt is requested
    }

    // ---- QS-WI-004: the tone-map cause taxonomy ----

    [Fact]
    public async Task Sdr_device_names_the_device_as_the_cause()
    {
        var svc = BuildService(preserveHdr: true);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), SdrClient(), "tok", LanIp);

        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemap);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemapServerPolicy);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemapCodec);
    }

    [Fact]
    public async Task Preserve_off_names_server_policy_for_an_hdr_capable_device()
    {
        var svc = BuildService(preserveHdr: false);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), HdrClient(), "tok", LanIp);

        Assert.True(plan.ToneMapPlanned);
        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemapServerPolicy);
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemap);
    }

    [Fact]
    public async Task Subtitle_burn_in_is_named_explicitly_when_it_forces_the_tonemap()
    {
        // HDR-capable client + PreserveHDR on: passthrough WOULD engage, but burned-in
        // subtitles force the conversion — the burn-in must be named (QS-WI-004).
        var svc = BuildService(preserveHdr: true);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), HdrItem(), HdrClient(subtitleTrack: 2), "tok", LanIp);

        Assert.True(plan.ToneMapPlanned);
        Assert.False(plan.IsHdr);
        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemapSubtitles);
        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.SubtitleBurnIn);
    }

    [Fact]
    public async Task Eight_bit_output_codec_is_named_when_it_blocks_passthrough()
    {
        // HDR-capable display whose decoder list has no hevc/av1: the negotiated output is
        // 8-bit h264, which cannot carry HDR — the plan says SDR up front and names the codec.
        var svc = BuildService(preserveHdr: true);
        var plan = await svc.ComputeStreamPlanAsync(
            Guid.NewGuid(), HdrItem(), HdrClient(hevc: false), "tok", LanIp);

        Assert.True(plan.ToneMapPlanned);
        Assert.False(plan.IsHdr); // the honesty fix: never promise HDR that h264 can't deliver
        Assert.Contains(plan.ReasonCodes, c => c.Code == StreamReasonCodes.HdrTonemapCodec);
    }

    [Fact]
    public async Task Hdr_passthrough_never_fires_the_guardrail()
    {
        // PreserveHDR + hevc-capable HDR client + no subtitles: HDR end-to-end, no tone-map
        // planned — the prompt keys off the PLAN, not the file.
        var svc = BuildService(preserveHdr: true);
        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), HdrItem(), HdrClient(), "tok", LanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.True(plan.IsHdr);
        Assert.False(plan.ToneMapPlanned);
        Assert.Null(plan.ToneMapPipeline);
        Assert.Null(plan.HdrTranscodePolicy);
    }

    [Fact]
    public async Task Sdr_source_never_fires_the_guardrail()
    {
        var item = HdrItem();
        item.VideoCodec = "mpeg2";
        var plan = await BuildSdrService().ComputeStreamPlanAsync(
            Guid.NewGuid(), item, SdrClient(), "tok", LanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.False(plan.ToneMapPlanned);
        Assert.Null(plan.HdrTranscodePolicy);
    }

    private static StreamPlanService BuildSdrService()
    {
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "mpeg2",
            AudioCodec = "mp3",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            Duration = 100,
        });

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");

        return new StreamPlanService(ffmpeg.Object, settings.Object,
            new Mock<IOpenClToneMapProbe>().Object, NullLogger<StreamPlanService>.Instance);
    }

    // ---- QS-WI-004: container named as the culprit when it alone forces the transcode ----

    [Fact]
    public async Task Container_alone_is_named_when_codecs_are_fine_but_it_cannot_be_repackaged()
    {
        // h264 + vorbis: both client-decodable, but vorbis cannot be muxed into fMP4-HLS
        // (RemuxAudioCodecs) and mkv can't direct-play for an mp4-only client — the old
        // code emitted the generic transcode.required here.
        var ffmpeg = new Mock<IFFmpegService>();
        ffmpeg.Setup(f => f.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            VideoCodec = "h264",
            AudioCodec = "vorbis",
            Resolution = "1920x1080",
            PixelFormat = "yuv420p",
            Duration = 100,
        });

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(0);
        settings.Setup(s => s.GetSettingAsync("ForceDirectPlayWhenPossible", It.IsAny<bool>())).ReturnsAsync(true);
        settings.Setup(s => s.GetSettingAsync("DefaultStreamingQuality", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("DefaultAudioChannels", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("OutputVideoCodec", It.IsAny<string>())).ReturnsAsync("auto");
        settings.Setup(s => s.GetSettingAsync("PreserveHDR", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("EnableAV1Encoding", It.IsAny<bool>())).ReturnsAsync(false);
        settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync("original");

        var svc = new StreamPlanService(ffmpeg.Object, settings.Object,
            new Mock<IOpenClToneMapProbe>().Object, NullLogger<StreamPlanService>.Instance);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "V",
            Path = "/v.mkv",
            VideoCodec = "h264",
            AudioCodec = "vorbis",
            Container = "mkv",
            Resolution = "1080p",
        };

        var plan = await svc.ComputeStreamPlanAsync(Guid.NewGuid(), item, SdrClient(), "tok", LanIp);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains(plan.ReasonCodes, c =>
            c.Code == StreamReasonCodes.ContainerUnsupported && c.Params["container"] == "mkv");
        Assert.DoesNotContain(plan.ReasonCodes, c => c.Code == StreamReasonCodes.TranscodeRequired);
    }
}
