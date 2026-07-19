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
    private readonly string _outputDir;

    public TranscodeProfileBuilderTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        _subtitles.Setup(s => s.GetSubtitleStreamIndexAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(0);
        // R-WI-012: text burn-in pre-extracts to burnin.ass; default the mock to success so the
        // burn-in branch is exercised (a loose mock's false would silently disable it).
        _subtitles.Setup(s => s.ExtractSubtitleToAssAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);
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
            _subtitles.Object);
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
    [InlineData("720p", "min(1280,iw)")]
    [InlineData("1080p", "min(1920,iw)")]
    [InlineData("4k", "min(3840,iw)")]
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
