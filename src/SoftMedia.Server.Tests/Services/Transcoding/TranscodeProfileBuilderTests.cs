using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// <summary>
/// Tier-1 stream-quality rules of the ffmpeg argument builder:
///   1. interlaced sources are deinterlaced on EVERY pipeline branch (browsers can't deinterlace),
///   2. scaling can never upscale past the source width (min(W,iw) clamp),
///   3. real downscales use lanczos.
/// Assertions run against the generated ffmpeg argument string — no ffmpeg is executed.
/// </summary>
public class TranscodeProfileBuilderTests : IDisposable
{
    private readonly Mock<IMediaProbeService> _probe = new();
    private readonly Mock<ISubtitleService> _subtitles = new();
    private readonly Mock<IOpenClToneMapProbe> _openClProbe = new();
    private readonly string _outputDir;

    public TranscodeProfileBuilderTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        _subtitles.Setup(s => s.GetSubtitleStreamIndexAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(0);
        // R-WI-012: text burn-in pre-extracts to burnin.ass; default the mock to success so the
        // burn-in branch is exercised (a loose mock's false would silently disable it).
        _subtitles.Setup(s => s.ExtractSubtitleToAssAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        // QS-WI-012: the OpenCL runtime is available by default; individual tests flip it off
        // to exercise the software fallback.
        _openClProbe.Setup(p => p.IsAvailableAsync()).ReturnsAsync(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    private TranscodeProfileBuilder Build()
    {
        var binaries = new Mock<IBinaryLocationService>();
        binaries.Setup(b => b.ResolveFFmpegPath()).Returns("ffmpeg");
        binaries.Setup(b => b.ResolveFFprobePath()).Returns("ffprobe");

        return new TranscodeProfileBuilder(
            NullLogger<TranscodeProfileBuilder>.Instance,
            binaries.Object,
            _probe.Object,
            _subtitles.Object,
            _openClProbe.Object);
    }

    private void SetupSource(string? fieldOrder = null, string pixelFormat = "yuv420p", string? colorTransfer = null)
    {
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            FieldOrder = fieldOrder,
            PixelFormat = pixelFormat,
            ColorTransfer = colorTransfer,
            FrameRate = 25,
            AudioCodec = "aac",   // source has audio → the audio stream gets pinned/mapped
            AudioChannels = 2,
        });
    }

    private async Task<string> ArgsAsync(TranscodeSettings settings, int? subtitleTrackIndex = null)
    {
        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg", settings, subtitleTrackIndex);
        return psi.Arguments;
    }

    // ---- Deinterlacing ----

    [Fact]
    public async Task Interlaced_source_gets_bwdif_on_the_software_path()
    {
        SetupSource(fieldOrder: "tt");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.Contains("-vf \"bwdif=mode=send_frame\"", args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("progressive")]
    public async Task Progressive_source_is_never_deinterlaced(string? fieldOrder)
    {
        SetupSource(fieldOrder: fieldOrder);

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "1080p" });

        Assert.DoesNotContain("bwdif", args);
        Assert.DoesNotContain("yadif", args);
    }

    [Fact]
    public async Task Interlaced_source_is_deinterlaced_before_the_downscale()
    {
        SetupSource(fieldOrder: "bb");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "720p" });

        Assert.Contains("bwdif=mode=send_frame,scale=", args);
    }

    [Fact]
    public async Task Interlaced_source_on_the_cuda_path_uses_yadif_cuda_before_scale_cuda()
    {
        SetupSource(fieldOrder: "tb");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "nvidia", MaxResolution = "1080p" });

        Assert.Contains("yadif_cuda=mode=send_frame,scale_cuda=", args);
        Assert.DoesNotContain("bwdif=", args); // software bwdif can't run on CUDA frames
    }

    [Fact]
    public async Task Interlaced_hdr_source_is_deinterlaced_at_the_head_of_the_tonemap_chain()
    {
        SetupSource(fieldOrder: "tt", pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "nvidia", MaxResolution = "original" });

        Assert.Contains("yadif_cuda=mode=send_frame,scale_cuda=", args);
        Assert.Contains("tonemap_cuda", args);
    }

    [Fact]
    public async Task Interlaced_source_with_text_subtitles_deinterlaces_before_the_subtitle_burn()
    {
        SetupSource(fieldOrder: "tt");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains("bwdif=mode=send_frame,subtitles=", args);
    }

    [Fact]
    public async Task Interlaced_source_with_bitmap_subtitles_deinterlaces_before_the_overlay()
    {
        SetupSource(fieldOrder: "tt");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("hdmv_pgs_subtitle");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains("[0:v]bwdif=mode=send_frame[dei];", args);
        Assert.Contains("[dei]scale2ref", args);
    }

    // ---- R-WI-012: burn-in path safety (temp-file extraction, no path in filter strings) ----

    [Theory]
    [InlineData(@"C:\media\It's Always Sunny (2005)\Don't Stop.mkv")]  // apostrophes — the old guard skipped these
    [InlineData(@"C:\media\Some Movie [x264] (2020)\a b.mkv")]         // brackets + spaces
    [InlineData(@"C:\med'ia:odd\c'lip.mkv")]                            // apostrophe + colon in the DIRECTORY too
    public async Task Text_burn_in_uses_extracted_session_file_never_the_media_path(string inputPath)
    {
        SetupSource();
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");

        var psi = await Build().BuildTranscodeArgumentsAsync(
            inputPath, _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        // The filter references ONLY the fixed relative filename (resolved via WorkingDirectory),
        // plus fontsdir=. so dumped embedded fonts are honoured.
        Assert.Contains($"subtitles={TranscodeProfileBuilder.BurnInSubtitleFileName}:fontsdir=.", psi.Arguments);
        Assert.DoesNotContain("subtitles='", psi.Arguments);           // the old quoted-path form is gone
        Assert.DoesNotContain(":si=", psi.Arguments);                  // extracted file has exactly one stream
        Assert.Equal(_outputDir, psi.WorkingDirectory);                // relative name resolves in the session dir

        // The media path appears ONLY as the quoted -i argument, never inside -vf.
        var vfStart = psi.Arguments.IndexOf("-vf ");
        Assert.True(vfStart >= 0);
        Assert.DoesNotContain("Sunny", psi.Arguments[vfStart..]);
        Assert.DoesNotContain(".mkv", psi.Arguments[vfStart..]);

        _subtitles.Verify(s => s.ExtractSubtitleToAssAsync(
            inputPath, 0, Path.Combine(_outputDir, TranscodeProfileBuilder.BurnInSubtitleFileName)), Times.Once);
        _subtitles.Verify(s => s.DumpFontAttachmentsAsync(inputPath, _outputDir), Times.Once); // fonts ride along
    }

    [Fact]
    public async Task Existing_session_burnin_file_is_reused_without_reextraction()
    {
        // ffmpeg restarts within a session re-enter the builder; a prior clean extraction's file
        // must be reused (strict extraction deletes partials, so existing+non-empty = valid).
        SetupSource();
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");
        Directory.CreateDirectory(_outputDir);
        File.WriteAllText(Path.Combine(_outputDir, TranscodeProfileBuilder.BurnInSubtitleFileName), "[Script Info]");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains($"subtitles={TranscodeProfileBuilder.BurnInSubtitleFileName}", args);
        _subtitles.Verify(s => s.ExtractSubtitleToAssAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Failed_extraction_degrades_to_no_burn_in_but_keeps_the_video_pipeline()
    {
        SetupSource(fieldOrder: "tt"); // interlaced so the fallback pipeline is observable
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");
        _subtitles.Setup(s => s.ExtractSubtitleToAssAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.DoesNotContain("subtitles=", args);          // no burn-in, but…
        Assert.Contains("bwdif=mode=send_frame", args);     // …the transcode itself still deinterlaces
    }

    [Fact]
    public async Task Bitmap_burn_in_never_extracts_a_text_file()
    {
        SetupSource();
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("hdmv_pgs_subtitle");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains("scale2ref", args); // overlay pipeline as before
        _subtitles.Verify(s => s.ExtractSubtitleToAssAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    // ---- Upscale clamp + lanczos ----

    [Theory]
    [InlineData("480p", "min(854,iw)")]
    [InlineData("720p", "min(1280,iw)")]
    [InlineData("1080p", "min(1920,iw)")]
    [InlineData("1440p", "min(2560,iw)")]   // numeric "{n}p" labels come from negotiated plans
    [InlineData("4k", "min(3840,iw)")]
    [InlineData("2160p", "min(3840,iw)")]   // "2160p" and "4k" are the same target
    [InlineData("4320p", "min(7680,iw)")]
    public async Task Software_scale_targets_are_clamped_to_source_width(string maxResolution, string expectedClamp)
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = maxResolution });

        Assert.Contains($"scale='{expectedClamp}':-2:flags=lanczos", args);
    }

    [Fact]
    public async Task Cuda_scale_targets_are_clamped_and_use_lanczos()
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "nvidia", MaxResolution = "1080p" });

        Assert.Contains("scale_cuda=w='min(1920,iw)':h=-2", args);
        Assert.Contains("interp_algo=lanczos", args);
    }

    [Fact]
    public async Task Original_resolution_adds_no_scale_filter()
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.DoesNotContain("scale=", args);
        Assert.DoesNotContain("-vf", args);
    }

    // ---- R-WI-003: remux (stream-copy) ----

    private string RemuxArgs(double? seek = null, int? audioTrackIndex = null) =>
        Build().BuildRemuxArguments(@"C:\media\movie.mkv", _outputDir, "seg", seek, audioTrackIndex).Arguments;

    [Fact]
    public void Remux_copies_streams_with_no_encoder_options()
    {
        var args = RemuxArgs();

        Assert.Contains("-c copy", args);
        // No re-encode: no video encoder, no bitrate cap, no filters.
        Assert.DoesNotContain("h264_nvenc", args);
        Assert.DoesNotContain("libx264", args);
        Assert.DoesNotContain("-maxrate", args);
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-filter_complex", args);
        // Audio is copied too — NOT the forced aac stereo of the transcode path (D-2).
        Assert.DoesNotContain("-c:a aac", args);
    }

    [Fact]
    public void Remux_uses_fmp4_segments_for_hevc_compatibility()
    {
        // fMP4 (not TS): copied HEVC won't play in TS on the clients that advertise HEVC.
        var args = RemuxArgs();

        Assert.Contains("-hls_segment_type fmp4", args);
        Assert.Contains("-hls_fmp4_init_filename init.mp4", args);
        Assert.Contains("seg_%03d.m4s", args);
        Assert.DoesNotContain(".ts", args);
    }

    [Fact]
    public void Remux_maps_video_and_default_audio()
    {
        var args = RemuxArgs();

        Assert.Contains("-map 0:v:0", args);
        Assert.Contains("-map 0:a:0", args);
    }

    [Fact]
    public void Remux_maps_selected_audio_track()
    {
        var args = RemuxArgs(audioTrackIndex: 2);

        Assert.Contains("-map 0:v:0", args);
        Assert.Contains("-map 0:2", args);
        Assert.DoesNotContain("-map 0:a:0", args);
    }

    [Fact]
    public void Remux_with_seek_uses_fast_seek_before_input_and_copyts()
    {
        var args = RemuxArgs(seek: 42);

        // Fast (keyframe) seek: -ss precedes -i, and -copyts keeps timestamps.
        var ssIndex = args.IndexOf("-ss 42.00", StringComparison.Ordinal);
        var inputIndex = args.IndexOf("-i ", StringComparison.Ordinal);
        Assert.True(ssIndex >= 0 && ssIndex < inputIndex, "fast seek must place -ss before -i");
        Assert.Contains("-copyts", args);
    }

    [Fact]
    public void Remux_without_seek_omits_seek_flags()
    {
        var args = RemuxArgs();

        Assert.DoesNotContain("-ss ", args);
        Assert.DoesNotContain("-copyts", args);
    }

    // ---- R-WI-004: audio ladder (copy → plan codec/channels → stereo AAC) ----

    private async Task<string> AudioArgsAsync(bool audioCopy, string? audioCodec, int audioChannels, int? audioTrackIndex = null)
    {
        SetupSource();
        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: null, seekPosition: null, readRate: null,
            audioTrackIndex: audioTrackIndex, maxBitrate: null,
            audioCopy: audioCopy, audioCodec: audioCodec, audioChannels: audioChannels);
        return psi.Arguments;
    }

    [Fact]
    public async Task Audio_copies_source_when_plan_says_copy_default_track()
    {
        var args = await AudioArgsAsync(audioCopy: true, audioCodec: "ac3", audioChannels: 6);

        Assert.Contains("-c:a copy", args);
        Assert.DoesNotContain("-c:a aac", args);
    }

    [Fact]
    public async Task Audio_copy_pins_first_audio_track_map()
    {
        // diff-review HIGH: the copy path must explicitly map 0:a:0 (the track the plan validated),
        // not rely on ffmpeg's implicit selection (which picks the highest-channel, possibly
        // undecodable, alternate track on a multi-track file).
        var args = await AudioArgsAsync(audioCopy: true, audioCodec: "ac3", audioChannels: 6);

        Assert.Contains("-map 0:a:0", args);
        Assert.Contains("-c:a copy", args);
    }

    [Fact]
    public async Task Audio_encodes_surround_ac3_5_1_not_forced_stereo()
    {
        // The D-2 regression: surround must survive as AC3 5.1, not be forced to stereo AAC.
        var args = await AudioArgsAsync(audioCopy: false, audioCodec: "ac3", audioChannels: 6);

        Assert.Contains("-c:a ac3 -ac 6", args);
        Assert.DoesNotContain("-c:a aac -ac 2", args);
    }

    [Fact]
    public async Task Audio_stereo_aac_when_plan_says_so()
    {
        var args = await AudioArgsAsync(audioCopy: false, audioCodec: "aac", audioChannels: 2);

        Assert.Contains("-c:a aac -ac 2", args);
    }

    [Fact]
    public async Task Audio_defaults_to_stereo_aac_when_no_plan()
    {
        // sid-less / no-plan request: unset audio params reproduce the old stereo-AAC behaviour.
        var args = await AudioArgsAsync(audioCopy: false, audioCodec: null, audioChannels: 0);

        Assert.Contains("-c:a aac -ac 2", args);
    }

    [Fact]
    public async Task Audio_copy_ignored_for_explicitly_selected_track()
    {
        // A selected non-default track encodes to neutral AAC without imposing the DEFAULT track's
        // negotiated channel count (which would up/downmix the selected track wrongly — diff-review).
        var args = await AudioArgsAsync(audioCopy: true, audioCodec: "ac3", audioChannels: 6, audioTrackIndex: 1);

        Assert.DoesNotContain("-c:a copy", args);
        Assert.Contains("-c:a aac", args);
        Assert.DoesNotContain("-ac 6", args); // selected track's own layout preserved
        Assert.Contains("-map 0:1", args);     // maps the selected track
    }

    [Fact]
    public async Task Explicitly_selected_track_still_respects_the_client_channel_ceiling()
    {
        // LIVE BUG: a 6-channel TrueHD track selected by a stereo-only browser was encoded as
        // 6-channel AAC (this branch omitted -ac entirely). Chrome cannot initialise a decoder for
        // 6ch AAC with an unknown layout: every SourceBuffer append errored and hls.js recreated the
        // buffer forever, so the movie fetched segments but never played. The ceiling must survive
        // explicit track selection.
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            PixelFormat = "yuv420p",
            FrameRate = 24,
            AudioCodec = "truehd",
            AudioChannels = 6, // source is 5.1
        });

        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: null, seekPosition: null, readRate: null,
            audioTrackIndex: 1, maxBitrate: null,
            audioCopy: false, audioCodec: "aac", audioChannels: 2); // client ceiling = stereo
        var args = psi.Arguments;

        Assert.Contains("-c:a aac -ac 2", args);
        Assert.DoesNotContain("-ac 6", args);
    }

    [Fact]
    public async Task Explicitly_selected_track_is_never_upmixed_above_the_source()
    {
        // The ceiling is a CAP, not a target: a stereo track on a surround-capable client must
        // stay stereo rather than being upmixed to 5.1.
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            PixelFormat = "yuv420p",
            FrameRate = 24,
            AudioCodec = "aac",
            AudioChannels = 2, // source is stereo
            AudioTracks = new List<AudioTrackInfo>
            {
                new() { Index = 0, StreamIndex = 1, Codec = "aac", Channels = 2 },
            },
        });

        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: null, seekPosition: null, readRate: null,
            audioTrackIndex: 1, maxBitrate: null,
            audioCopy: false, audioCodec: "aac", audioChannels: 6); // client could take 5.1

        Assert.Contains("-c:a aac -ac 2", psi.Arguments);
    }

    // ---- SR-WI-023/QS-WI-012: HDR tone mapping (nvidia CUDA / intel+amd OpenCL / software
    // ---- fallback) + color metadata ----

    /// <summary>The exact software HDR→SDR chain (with the default hable operator).</summary>
    private const string SoftwareToneMapChain =
        "zscale=t=linear:npl=100,tonemap=hable,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>The exact OpenCL HDR→SDR chain (QS-WI-012, default hable operator).</summary>
    private const string OpenClToneMapChain =
        "format=p010le,hwupload,tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=hable:desat=0,hwdownload,format=nv12";

    private const string Bt709Tags = "-color_primaries bt709 -color_trc bt709 -colorspace bt709";

    [Theory]
    [InlineData("smpte2084")]     // HDR10 (PQ)
    [InlineData("arib-std-b67")]  // HLG
    public async Task Hdr_source_without_hw_accel_engages_the_software_tonemap_chain_with_bt709_tags(string transfer)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: transfer);

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.Contains($"-vf \"{SoftwareToneMapChain}\"", args);
        Assert.Contains(Bt709Tags, args);
        Assert.DoesNotContain("tonemap_cuda", args);   // CUDA chain is nvidia-only
        Assert.DoesNotContain("tonemap_opencl", args); // OpenCL chain is intel/amd-only
        Assert.DoesNotContain("bt2020", args);         // output is SDR, never tagged HDR
    }

    [Theory]
    [InlineData("intel", "smpte2084")]
    [InlineData("amd", "smpte2084")]
    [InlineData("intel", "arib-std-b67")]
    [InlineData("amd", "arib-std-b67")]
    public async Task Hdr_source_on_intel_amd_engages_the_opencl_tonemap_chain_with_bt709_tags(string hw, string transfer)
    {
        // QS-WI-012: with a working OpenCL runtime, Intel/AMD tone-map on the GPU.
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: transfer);

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.Contains($"-vf \"{OpenClToneMapChain}\"", args);
        Assert.Contains("-init_hw_device opencl=ocl -filter_hw_device ocl", args);
        Assert.Contains(Bt709Tags, args);
        Assert.DoesNotContain("zscale", args);        // the software chain must not double up
        Assert.DoesNotContain("tonemap_cuda", args);
        Assert.DoesNotContain("bt2020", args);
    }

    [Theory]
    [InlineData("intel")]
    [InlineData("amd")]
    public async Task Hdr_on_intel_amd_without_opencl_falls_back_to_the_software_chain(string hw)
    {
        // QS-WI-012: the software zscale/tonemap chain is the universal fallback — never
        // removed. No OpenCL runtime → no OpenCL device init (which would kill ffmpeg).
        _openClProbe.Setup(p => p.IsAvailableAsync()).ReturnsAsync(false);
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.Contains($"-vf \"{SoftwareToneMapChain}\"", args);
        Assert.DoesNotContain("tonemap_opencl", args);
        Assert.DoesNotContain("-init_hw_device opencl", args);
    }

    [Fact]
    public async Task OpenCl_chain_scales_in_software_before_the_gpu_upload()
    {
        // Fewer pixels through the GPU hop: downscale precedes hwupload, mirroring the
        // scale-before-tonemap ordering of the CUDA and software chains.
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "intel", MaxResolution = "720p" });

        Assert.Contains($"scale='min(1280,iw)':-2:flags=lanczos,{OpenClToneMapChain}", args);
    }

    [Fact]
    public async Task Interlaced_hdr_source_deinterlaces_at_the_head_of_the_opencl_chain()
    {
        SetupSource(fieldOrder: "tt", pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "amd", MaxResolution = "original" });

        Assert.Contains($"bwdif=mode=send_frame,{OpenClToneMapChain}", args);
    }

    [Fact]
    public async Task Hdr_text_subtitles_burn_after_the_opencl_tonemap()
    {
        // The chain ends in system-memory nv12 (hwdownload), so bt709 subtitle colors land
        // on already-tone-mapped frames — same shape as the CUDA chain's subtitle tail.
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "intel", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains($"{OpenClToneMapChain},subtitles=", args);
    }

    [Theory]
    [InlineData("intel", "h264_qsv")]
    [InlineData("amd", "h264_amf")]
    public async Task OpenCl_tonemap_keeps_the_hardware_encoder(string hw, string encoder)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.Contains($"-c:v {encoder}", args);
    }

    [Fact]
    public async Task OpenCl_chain_uses_the_configured_tonemap_operator()
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "intel",
            MaxResolution = "original",
            ToneMappingAlgorithm = "reinhard",
        });

        Assert.Contains("tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=reinhard", args);
    }

    // ---- QS-WI-012: SelectToneMapPipeline — the single pipeline authority ----

    [Theory]
    [InlineData("nvidia", true, ToneMapPipeline.Cuda)]
    [InlineData("nvidia", false, ToneMapPipeline.Cuda)]   // CUDA never depends on OpenCL
    [InlineData("intel", true, ToneMapPipeline.OpenCl)]
    [InlineData("intel", false, ToneMapPipeline.Software)]
    [InlineData("amd", true, ToneMapPipeline.OpenCl)]
    [InlineData("amd", false, ToneMapPipeline.Software)]
    [InlineData("none", true, ToneMapPipeline.Software)]  // OpenCL is only wired for intel/amd
    [InlineData("none", false, ToneMapPipeline.Software)]
    public void Pipeline_authority_maps_each_hwaccel_to_its_tonemap_pipeline(string hw, bool openCl, ToneMapPipeline expected)
    {
        var pipeline = TranscodeProfileBuilder.SelectToneMapPipeline(
            hw, sourceIsHdr: true, preserveHdr: false, outputVideoCodec: "h264",
            subtitleBurnIn: false, openClToneMapAvailable: openCl);

        Assert.Equal(expected, pipeline);
    }

    [Theory]
    [InlineData("nvidia")]
    [InlineData("intel")]
    [InlineData("none")]
    public void Pipeline_authority_returns_none_for_sdr_sources(string hw)
    {
        Assert.Equal(ToneMapPipeline.None, TranscodeProfileBuilder.SelectToneMapPipeline(
            hw, sourceIsHdr: false, preserveHdr: false, outputVideoCodec: "h264",
            subtitleBurnIn: false, openClToneMapAvailable: true));
    }

    [Fact]
    public void Pipeline_authority_honours_hdr_passthrough_only_for_hdr_capable_codecs()
    {
        // PreserveHDR + hevc → passthrough (no tone-map)…
        Assert.Equal(ToneMapPipeline.None, TranscodeProfileBuilder.SelectToneMapPipeline(
            "nvidia", sourceIsHdr: true, preserveHdr: true, outputVideoCodec: "hevc",
            subtitleBurnIn: false, openClToneMapAvailable: false));
        // …but h264 output can't carry HDR (SR-WI-023 #5) — tone-map despite PreserveHDR…
        Assert.Equal(ToneMapPipeline.Cuda, TranscodeProfileBuilder.SelectToneMapPipeline(
            "nvidia", sourceIsHdr: true, preserveHdr: true, outputVideoCodec: "h264",
            subtitleBurnIn: false, openClToneMapAvailable: false));
        // …and subtitle burn-in forces the tone-map even for hevc passthrough.
        Assert.Equal(ToneMapPipeline.Cuda, TranscodeProfileBuilder.SelectToneMapPipeline(
            "nvidia", sourceIsHdr: true, preserveHdr: true, outputVideoCodec: "hevc",
            subtitleBurnIn: true, openClToneMapAvailable: false));
    }

    // ---- QS-WI-006: default transcode ladder (CVBR ceiling when nothing was negotiated) ----

    [Theory]
    [InlineData("480p", "-maxrate 2500k -bufsize 5000k")]
    [InlineData("720p", "-maxrate 5000k -bufsize 10000k")]
    [InlineData("1080p", "-maxrate 9000k -bufsize 18000k")]
    [InlineData("1440p", "-maxrate 14000k -bufsize 28000k")]
    [InlineData("4k", "-maxrate 22000k -bufsize 44000k")]
    [InlineData("2160p", "-maxrate 22000k -bufsize 44000k")]
    [InlineData("4320p", "-maxrate 22000k -bufsize 44000k")] // above the top rung: 2160 ceiling
    public async Task Uncapped_transcode_gets_the_ladder_default_ceiling_for_its_target(string maxRes, string expected)
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = maxRes });

        Assert.Contains(expected, args);
    }

    [Fact]
    public async Task Negotiated_bitrate_cap_replaces_the_ladder_default_outright()
    {
        SetupSource();

        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "1080p" },
            subtitleTrackIndex: null, seekPosition: null, readRate: null,
            audioTrackIndex: null, maxBitrate: 3000);

        Assert.Contains("-maxrate 3000k", psi.Arguments);
        Assert.DoesNotContain("-maxrate 9000k", psi.Arguments);
    }

    [Fact]
    public async Task Ladder_default_uses_the_source_height_when_resolution_is_original()
    {
        // Never-upscale clamp: with MaxResolution=original a 1080p source picks the 1080 rung.
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            PixelFormat = "yuv420p",
            FrameRate = 25,
            Resolution = "1920x1080",
            AudioCodec = "aac",
            AudioChannels = 2,
        });

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.Contains("-maxrate 9000k", args);
    }

    [Fact]
    public async Task Ladder_default_is_skipped_when_the_output_height_is_unknown()
    {
        // Unknown source size + "original": never guess low — no ceiling at all.
        SetupSource(); // probe has no Resolution

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.DoesNotContain("-maxrate", args);
    }

    [Fact]
    public async Task Ladder_default_scales_down_for_hevc_output()
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "none",
            MaxResolution = "1080p",
            OutputVideoCodec = "hevc",
        });

        Assert.Contains("-maxrate 5400k", args); // 9000 × 0.6
    }

    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public async Task Hdr_source_on_nvidia_keeps_the_cuda_tonemap_chain_with_bt709_tags(string transfer)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: transfer);

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "nvidia", MaxResolution = "original" });

        Assert.Contains("tonemap_cuda", args);
        Assert.DoesNotContain("zscale", args); // software chain must not double up on CUDA frames
        Assert.Contains(Bt709Tags, args);
    }

    [Theory]
    [InlineData("nvidia")]
    [InlineData("intel")]
    [InlineData("amd")]
    [InlineData("none")]
    public async Task Sdr_source_never_tone_maps_but_is_still_tagged_bt709(string hw)
    {
        SetupSource(); // yuv420p, no HDR transfer

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.DoesNotContain("tonemap", args);
        Assert.DoesNotContain("zscale", args);
        Assert.Contains(Bt709Tags, args); // SR-WI-023 #4: color metadata on ALL encode outputs
    }

    [Theory]
    [InlineData("intel", "-hwaccel qsv", "h264_qsv")]
    [InlineData("amd", "-hwaccel d3d11va", "h264_amf")]
    public async Task Hdr_on_intel_amd_forces_software_decode_but_keeps_the_hardware_encoder(string hw, string hwDecodeFlag, string encoder)
    {
        // QS-WI-012: the OpenCL chain's hwupload consumes system-memory frames, so decode
        // stays software (zero-copy QSV/D3D11→OpenCL interop is driver-fragile)…
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.DoesNotContain(hwDecodeFlag, args);
        // …while the hardware encoder stays (it accepts system-memory frames).
        Assert.Contains($"-c:v {encoder}", args);
        Assert.Contains($"-vf \"{OpenClToneMapChain}\"", args);
    }

    [Theory]
    [InlineData("intel", "-hwaccel qsv")]
    [InlineData("amd", "-hwaccel d3d11va")]
    public async Task Sdr_on_intel_amd_keeps_hardware_decode(string hw, string hwDecodeFlag)
    {
        SetupSource();

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = hw, MaxResolution = "original" });

        Assert.Contains(hwDecodeFlag, args);
    }

    [Theory]
    [InlineData("reinhard", "tonemap=reinhard")]
    [InlineData("mobius", "tonemap=mobius")]
    [InlineData("not-a-real-operator", "tonemap=hable")] // unknown falls back to hable
    public async Task Software_chain_uses_the_configured_tonemap_operator(string setting, string expected)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "none",
            MaxResolution = "original",
            ToneMappingAlgorithm = setting,
        });

        Assert.Contains(expected, args);
    }

    [Fact]
    public async Task Software_tonemap_composes_after_the_downscale_like_the_cuda_chain()
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "720p" });

        // Scale FIRST (fewer pixels through the expensive zscale linearisation), tonemap second —
        // mirroring scale_cuda → tonemap_cuda.
        Assert.Contains($"scale='min(1280,iw)':-2:flags=lanczos,{SoftwareToneMapChain}", args);
    }

    [Fact]
    public async Task Interlaced_hdr_source_deinterlaces_at_the_head_of_the_software_chain()
    {
        SetupSource(fieldOrder: "tt", pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" });

        Assert.Contains($"bwdif=mode=send_frame,{SoftwareToneMapChain}", args);
    }

    [Fact]
    public async Task Hdr_text_subtitles_burn_after_the_software_tonemap()
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        // bt709 subtitle colors must land on already-tone-mapped bt709 frames.
        Assert.Contains($"{SoftwareToneMapChain},subtitles=", args);
    }

    [Fact]
    public async Task Hdr_bitmap_subtitles_overlay_on_the_tone_mapped_video()
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("hdmv_pgs_subtitle");

        var args = await ArgsAsync(
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: 2);

        Assert.Contains($"[0:v]{SoftwareToneMapChain}[tm];", args);
        Assert.Contains("[tm]scale2ref", args);
    }

    // ---- SR-WI-023 #4/#5: PreserveHDR passthrough signaling and the h264 override ----

    [Theory]
    [InlineData("smpte2084", "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc")]     // HDR10
    [InlineData("arib-std-b67", "-color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc")] // HLG
    public async Task Preserved_hdr_into_hevc_passes_through_with_source_color_signaling(string transfer, string expectedTags)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: transfer);

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "none",
            MaxResolution = "original",
            OutputVideoCodec = "hevc",
            PreserveHDR = true,
        });

        Assert.DoesNotContain("tonemap", args); // passthrough: no tone mapping
        Assert.DoesNotContain("zscale", args);
        Assert.Contains(expectedTags, args);    // the fMP4 path no longer ships unsignaled HDR
        Assert.Contains("-hls_segment_type fmp4", args);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("auto")] // auto resolves to h264
    public async Task Preserve_hdr_with_h264_output_is_overridden_to_tone_mapping(string codecSetting)
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "none",
            MaxResolution = "original",
            OutputVideoCodec = codecSetting,
            PreserveHDR = true,
        });

        // h264 is 8-bit here: "preserving" PQ would squash it into gray. Tone-map instead.
        Assert.Contains(SoftwareToneMapChain, args);
        Assert.Contains(Bt709Tags, args);
        Assert.DoesNotContain("bt2020", args);
    }

    [Fact]
    public async Task Preserve_hdr_with_h264_output_on_nvidia_is_overridden_to_cuda_tone_mapping()
    {
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");

        var args = await ArgsAsync(new TranscodeSettings
        {
            HardwareAcceleration = "nvidia",
            MaxResolution = "original",
            OutputVideoCodec = "h264",
            PreserveHDR = true,
        });

        Assert.Contains("tonemap_cuda", args);
        Assert.Contains(Bt709Tags, args);
        Assert.DoesNotContain("bt2020", args);
    }

    [Fact]
    public async Task Preserve_hdr_with_subtitles_still_tone_maps_on_the_software_path()
    {
        // The nvidia path always forced tone mapping for burn-in; the software chain must too.
        SetupSource(pixelFormat: "yuv420p10le", colorTransfer: "smpte2084");
        _probe.Setup(p => p.ProbeSubtitleCodecAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("subrip");

        var args = await ArgsAsync(
            new TranscodeSettings
            {
                HardwareAcceleration = "none",
                MaxResolution = "original",
                OutputVideoCodec = "hevc",
                PreserveHDR = true,
            },
            subtitleTrackIndex: 2);

        Assert.Contains($"{SoftwareToneMapChain},subtitles=", args);
        Assert.Contains(Bt709Tags, args);
    }

    [Fact]
    public async Task Selected_track_channels_resolve_by_ABSOLUTE_stream_index_not_the_primary_track()
    {
        // Review catch: matching the audio-RELATIVE index (or falling back to the PRIMARY track's
        // channel count) silently resolves the wrong track on a multi-track file. Here the default
        // track is 5.1 and the user picked the stereo commentary; a surround-capable client must
        // still get STEREO, not an upmix of the commentary to 5.1.
        _probe.Setup(p => p.ProbeMediaAsync(It.IsAny<string>())).ReturnsAsync(new MediaProbeResult
        {
            PixelFormat = "yuv420p",
            FrameRate = 24,
            AudioCodec = "ac3",
            AudioChannels = 6, // PRIMARY track is 5.1 — the misleading fallback
            AudioTracks = new List<AudioTrackInfo>
            {
                new() { Index = 0, StreamIndex = 1, Codec = "ac3", Channels = 6, IsDefault = true },
                new() { Index = 1, StreamIndex = 2, Codec = "aac", Channels = 2, Title = "Commentary" },
            },
        });

        var psi = await Build().BuildTranscodeArgumentsAsync(
            @"C:\media\movie.mkv", _outputDir, "seg",
            new TranscodeSettings { HardwareAcceleration = "none", MaxResolution = "original" },
            subtitleTrackIndex: null, seekPosition: null, readRate: null,
            audioTrackIndex: 2, maxBitrate: null, // absolute stream index of the stereo commentary
            audioCopy: false, audioCodec: "ac3", audioChannels: 6);

        Assert.Contains("-c:a aac -ac 2", psi.Arguments);
        Assert.DoesNotContain("-ac 6", psi.Arguments);
        Assert.Contains("-map 0:2", psi.Arguments);
    }
}
