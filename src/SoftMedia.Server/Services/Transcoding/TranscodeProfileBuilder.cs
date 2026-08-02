using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SoftMedia.Server.Services.Transcoding;

public class TranscodeSettings
{
    public bool EnableTranscoding { get; set; } = true;
    public string HardwareAcceleration { get; set; } = "none";
    public string Preset { get; set; } = "veryfast";
    public int ThreadCount { get; set; } = 0;
    public string MaxResolution { get; set; } = "original";
    public int CRF { get; set; } = 23;
    public string OutputVideoCodec { get; set; } = "auto";
    public string ToneMappingAlgorithm { get; set; } = "hable";
    public bool PreserveHDR { get; set; } = false;
}

/// <summary>
/// QS-WI-012 — which tone-map implementation a given plan will run. Selected by
/// <see cref="TranscodeProfileBuilder.SelectToneMapPipeline"/>, the SINGLE authority both the
/// ffmpeg argument builder and the stream planner consult, so the QS-WI-005 guardrail's
/// "runs in software" line always reflects the pipeline that will actually execute.
/// </summary>
public enum ToneMapPipeline
{
    /// <summary>No tone-map: SDR source, or HDR passthrough engaged.</summary>
    None = 0,
    /// <summary>Fully hardware NVIDIA chain (scale_cuda + tonemap_cuda).</summary>
    Cuda,
    /// <summary>GPU-compute chain for Intel/AMD (hwupload + tonemap_opencl + hwdownload).</summary>
    OpenCl,
    /// <summary>CPU zscale/tonemap chain — the universal fallback, never removed.</summary>
    Software,
}

public interface ITranscodeProfileBuilder
{
    Task<ProcessStartInfo> BuildTranscodeArgumentsAsync(
        string inputPath,
        string outputDir,
        string segmentPrefix,
        TranscodeSettings settings,
        int? subtitleTrackIndex = null,
        double? seekPosition = null,
        double? readRate = null,
        int? audioTrackIndex = null,
        int? maxBitrate = null,
        bool audioCopy = false,
        string? audioCodec = null,
        int audioChannels = 0);

    /// <summary>
    /// Builds a REMUX (stream-copy) HLS command: the already-compatible video + audio streams are
    /// copied (<c>-c copy</c>) into fMP4 segments with no decode/encode (R-WI-003). Used when the
    /// negotiated plan is <see cref="DTOs.PlaybackMethod.Remux"/>, replacing the old behaviour
    /// where "remux" silently re-encoded through the full transcode path.
    /// </summary>
    ProcessStartInfo BuildRemuxArguments(
        string inputPath,
        string outputDir,
        string segmentPrefix,
        double? seekPosition = null,
        int? audioTrackIndex = null);
}

public class TranscodeProfileBuilder : ITranscodeProfileBuilder
{
    /// <summary>
    /// R-WI-012 — fixed name of the pre-extracted burn-in subtitle file inside the session
    /// directory. Referenced in the subtitles= filter as a bare relative filename (the ffmpeg
    /// process's WorkingDirectory is the session dir), so user media paths — apostrophes,
    /// brackets, colons and all — never enter a filter string. Cleaned up with the session
    /// directory (StopSession / TranscodeSegmentCleanupService).
    /// </summary>
    public const string BurnInSubtitleFileName = "burnin.ass";

    /// <summary>
    /// SR-WI-023: one-time-per-server-run latch for the "software tone mapping is CPU-intensive"
    /// warning (0 = not yet logged). Static on purpose — the builder is registered transient.
    /// </summary>
    private static int _softwareToneMapWarned;

    private readonly ILogger<TranscodeProfileBuilder> _logger;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ISubtitleService _subtitleService;
    private readonly IOpenClToneMapProbe _openClProbe;

    public TranscodeProfileBuilder(
        ILogger<TranscodeProfileBuilder> logger,
        IBinaryLocationService binaryLocationService,
        IMediaProbeService mediaProbeService,
        ISubtitleService subtitleService,
        IOpenClToneMapProbe openClProbe)
    {
        _logger = logger;
        _binaryLocationService = binaryLocationService;
        _mediaProbeService = mediaProbeService;
        _subtitleService = subtitleService;
        _openClProbe = openClProbe;
    }

    /// <summary>
    /// QS-WI-012 — the ONE place that decides which tone-map pipeline (if any) a transcode
    /// runs. The stream planner calls this with the NEGOTIATED values to publish the
    /// QS-WI-005 guardrail facts (ToneMapPlanned / ToneMapIsSoftware), and the argument
    /// builder calls it with the same values at ffmpeg start — so the pre-play prompt can
    /// never disagree with the pipeline that actually executes, and landing a new hardware
    /// pipeline here flips the prompt's resource line off with no prompt-code changes.
    /// </summary>
    /// <param name="preserveHdr">Whether HDR passthrough is requested for this transcode
    /// (the negotiated hdr flag — server PreserveHDR AND client HDR support).</param>
    /// <param name="outputVideoCodec">The negotiated output codec; 8-bit h264/auto cannot
    /// carry HDR, so passthrough is overridden to tone-mapping (SR-WI-023 #5).</param>
    /// <param name="subtitleBurnIn">Burn-in draws SDR subtitles, which forces tone-mapping
    /// even when passthrough would otherwise engage.</param>
    /// <param name="openClToneMapAvailable">Result of <see cref="IOpenClToneMapProbe"/> —
    /// when the OpenCL runtime is missing, Intel/AMD fall back to the software chain.</param>
    public static ToneMapPipeline SelectToneMapPipeline(
        string hardwareAcceleration, bool sourceIsHdr, bool preserveHdr, string outputVideoCodec,
        bool subtitleBurnIn, bool openClToneMapAvailable)
    {
        if (!sourceIsHdr) return ToneMapPipeline.None;

        var passthrough = preserveHdr && CodecCanCarryHdr(outputVideoCodec) && !subtitleBurnIn;
        if (passthrough) return ToneMapPipeline.None;

        return hardwareAcceleration.ToLowerInvariant() switch
        {
            "nvidia" => ToneMapPipeline.Cuda,
            "intel" or "amd" when openClToneMapAvailable => ToneMapPipeline.OpenCl,
            _ => ToneMapPipeline.Software,
        };
    }

    public async Task<ProcessStartInfo> BuildTranscodeArgumentsAsync(
        string inputPath,
        string outputDir,
        string segmentPrefix,
        TranscodeSettings settings,
        int? subtitleTrackIndex = null,
        double? seekPosition = null,
        double? readRate = null,
        int? audioTrackIndex = null,
        int? maxBitrate = null,
        bool audioCopy = false,
        string? audioCodec = null,
        int audioChannels = 0)
    {
        Directory.CreateDirectory(outputDir);

        var playlistPath = Path.Combine(outputDir, "master.m3u8");

        // Probe media to check for HDR/10-bit
        var probe = await _mediaProbeService.ProbeMediaAsync(inputPath);
        bool is10Bit = probe != null && Is10BitOrHdr(probe.PixelFormat, probe.ColorTransfer);
        if (is10Bit)
        {
            _logger.LogInformation("Detected 10-bit/HDR content (PixelFormat: {Fmt}). Using tone mapping pipeline.", probe?.PixelFormat ?? "unknown");
        }

        // Interlaced sources (DVD-era rips, broadcast captures) MUST be deinterlaced here:
        // browsers do not deinterlace, so combing would reach the screen otherwise. Software
        // paths use bwdif (better edges than yadif); CUDA-frame paths use yadif_cuda (available
        // on every ffmpeg build with CUDA, unlike bwdif_cuda which needs 6.0+). mode=send_frame
        // keeps the original frame rate.
        bool isInterlaced = probe?.IsInterlaced == true;
        if (isInterlaced)
        {
            _logger.LogInformation("Detected interlaced content (field_order: {FieldOrder}). Deinterlacing.", probe!.FieldOrder);
        }

        // Determine subtitle codec type FIRST (needed to decide seek strategy)
        bool hasSubtitleOverlay = subtitleTrackIndex.HasValue;
        string? subtitleCodec = null;
        bool useTextSubtitles = false;
        
        if (hasSubtitleOverlay)
        {
            subtitleCodec = await _mediaProbeService.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex!.Value);
            _logger.LogInformation("Subtitle track {Index} codec: {Codec}", subtitleTrackIndex, subtitleCodec ?? "unknown");
            useTextSubtitles = !IsBitmapSubtitleCodec(subtitleCodec);

            // R-WI-012: text burn-in no longer feeds the media path into the subtitles= filter
            // (ffmpeg's two-level filter quoting broke on apostrophes; the old workaround just
            // skipped burn-in for such paths). Instead, pre-extract the track to a fixed-name
            // .ass file in the session dir — the filter then references a bare relative filename
            // (WorkingDirectory is the session dir), so the media path never needs escaping.
            if (useTextSubtitles)
            {
                // Reuse a previously-extracted file in this session dir (ffmpeg restarts within a
                // session re-enter here; extraction is exit-code-strict and deletes partial output
                // on failure, so an existing non-empty file always means a prior clean extraction).
                var burnInPath = Path.Combine(outputDir, BurnInSubtitleFileName);
                var extracted = File.Exists(burnInPath) && new FileInfo(burnInPath).Length > 0;
                if (!extracted)
                {
                    var subtitleRelativeIndex = await _subtitleService.GetSubtitleStreamIndexAsync(inputPath, subtitleTrackIndex.Value);
                    extracted = await _subtitleService.ExtractSubtitleToAssAsync(inputPath, subtitleRelativeIndex, burnInPath);
                    if (extracted)
                    {
                        // Typeset ASS subs depend on the source's embedded fonts; the extracted
                        // .ass alone would render with fallback fonts. Best-effort, sanitized dump
                        // for the filter's :fontsdir=. to pick up.
                        await _subtitleService.DumpFontAttachmentsAsync(inputPath, outputDir);
                    }
                }

                if (!extracted)
                {
                    // Same graceful degradation the old guard had, but only on REAL failure:
                    // stream without subtitles rather than failing the whole transcode.
                    _logger.LogWarning("Burn-in subtitle extraction failed for {Path}; streaming without burned subtitles.", inputPath);
                    hasSubtitleOverlay = false;
                    useTextSubtitles = false;
                }
            }
        }

        // QS-WI-006: when NO bitrate ceiling was negotiated (no server/user/network cap, no
        // client ask), apply the documented ladder default for the target resolution as the
        // CVBR ceiling. CRF stays the quality driver; the ceiling only trims pathological
        // spikes (4K HDR grain under CRF alone can burst far past what any player needs).
        var effectiveMaxBitrate = maxBitrate is > 0
            ? maxBitrate
            : GetDefaultLadderMaxRateKbps(settings.MaxResolution, ParseHeightFromResolution(probe?.Resolution), settings.OutputVideoCodec);

        var argumentBuilder = new StringBuilder();

        // Thread count
        if (settings.ThreadCount > 0)
        {
            argumentBuilder.Append($"-threads {settings.ThreadCount} ");
        }

        // SEEK STRATEGY
        const double MaxSlowSeekSeconds = 60.0;
        bool seekIsTooLarge = seekPosition.HasValue && seekPosition.Value > MaxSlowSeekSeconds;
        bool useFastSeek = !useTextSubtitles || seekIsTooLarge;
        
        if (seekIsTooLarge && useTextSubtitles)
        {
            _logger.LogInformation("Using fast seek for large position {Seek}s (slow seek would be too slow)", seekPosition ?? 0);
        }
        
        if (useFastSeek && seekPosition.HasValue && seekPosition.Value > 0)
        {
            // Fast seek: -ss before -i. 
            // We use -copyts after -i (standard professional approach) to preserve timestamps for subtitle sync.
            argumentBuilder.Append($"-ss {seekPosition.Value:F2} ");
        }

        // Add read rate
        if (readRate.HasValue && readRate.Value > 0)
        {
            argumentBuilder.Append($"-readrate {readRate.Value:F1} ");
        }
        
        // --- 10-BIT / HDR HANDLING ---
        bool isHdr = probe != null && IsHdr(probe.ColorTransfer);
        var hwAccelLower = settings.HardwareAcceleration.ToLower();
        bool skipTonemapping = settings.PreserveHDR && isHdr;

        // SR-WI-023 #5: PreserveHDR is only honourable when the negotiated output codec can carry
        // 10-bit HDR (hevc/av1). Every h264 encoder here emits 8-bit yuv420p, so "preserving" a
        // PQ/HLG source into h264 would squash it into washed-out gray — tone-map instead.
        if (skipTonemapping && !CodecCanCarryHdr(settings.OutputVideoCodec))
        {
            _logger.LogInformation(
                "PreserveHDR requested but output codec {Codec} is 8-bit h264: disabling HDR passthrough and tone mapping instead (SR-WI-023).",
                settings.OutputVideoCodec);
            skipTonemapping = false;
        }

        // Smart HDR Override: If subtitles are active on HDR content, we MUST tone map to burn them in accurately.
        bool forceToneMappingForSubtitles = isHdr && hasSubtitleOverlay;

        // QS-WI-012: pipeline choice goes through the single SelectToneMapPipeline authority
        // (nvidia → CUDA, intel/amd → OpenCL when the runtime works, else the SR-WI-023
        // software zscale+tonemap chain). Probe only when the answer can matter — the result
        // is cached for the server run after the first call.
        var openClAvailable = isHdr && hwAccelLower is "intel" or "amd"
            && await _openClProbe.IsAvailableAsync();
        var toneMapPipeline = SelectToneMapPipeline(
            settings.HardwareAcceleration, isHdr, settings.PreserveHDR, settings.OutputVideoCodec,
            hasSubtitleOverlay, openClAvailable);
        bool useToneMappingPipeline = toneMapPipeline == ToneMapPipeline.Cuda;
        bool useOpenClToneMap = toneMapPipeline == ToneMapPipeline.OpenCl;
        bool useSoftwareToneMap = toneMapPipeline == ToneMapPipeline.Software;
        bool toneMapActive = toneMapPipeline != ToneMapPipeline.None;

        if (skipTonemapping && !forceToneMappingForSubtitles)
        {
            _logger.LogInformation("PreserveHDR enabled: skipping tonemapping for 10-bit/HDR content");
        }
        else if (skipTonemapping && forceToneMappingForSubtitles)
        {
            _logger.LogInformation("PreserveHDR enabled but OVERRIDDEN: tone mapping forced for subtitle burn-in compatibility");
        }

        if (useSoftwareToneMap && Interlocked.Exchange(ref _softwareToneMapWarned, 1) == 0)
        {
            _logger.LogWarning(
                "Software tone mapping engaged for HDR content — this is CPU-intensive. " +
                "Configure hardware acceleration (Settings > Transcoding) to offload it to the GPU. " +
                "This warning is logged once per server run.");
        }
        if (useOpenClToneMap)
        {
            _logger.LogInformation(
                "OpenCL tone mapping engaged for HDR content ({HwAccel}): the HDR→SDR conversion runs on the GPU (QS-WI-012).",
                settings.HardwareAcceleration);
        }

        // HARDWARE DECODE
        // SR-WI-023 #3: when the SOFTWARE tone-map chain is engaged, the decoder must produce
        // system-memory frames — zscale/tonemap cannot consume QSV/D3D11VA hardware frames. Force
        // software decode for the session; the hardware ENCODERS (h264_qsv/h264_amf/…) still apply
        // because they accept system-memory input frames.
        // QS-WI-012: the OpenCL chain also decodes in software (its hwupload consumes
        // system-memory frames; zero-copy QSV/D3D11→OpenCL interop is driver-fragile) — the
        // expensive stage, the tone-map math itself, is what moves onto the GPU.
        var hwDecodeOptions = useSoftwareToneMap || useOpenClToneMap
            ? string.Empty
            : GetHardwareDecodeOptions(settings.HardwareAcceleration, hasSubtitleOverlay, useToneMappingPipeline);
        if ((useSoftwareToneMap || useOpenClToneMap) && hwAccelLower is "intel" or "amd")
        {
            _logger.LogInformation(
                "HDR source with {HwAccel} acceleration: forcing software decode so the {Chain} tone-map chain can run (hardware encode unaffected).",
                settings.HardwareAcceleration, useOpenClToneMap ? "OpenCL" : "software");
        }
        if (!string.IsNullOrEmpty(hwDecodeOptions))
        {
            argumentBuilder.Append(hwDecodeOptions);
            _logger.LogInformation("Using hardware decode: {HwDecode}", hwDecodeOptions.Trim());
        }
        if (useOpenClToneMap)
        {
            // The OpenCL filter device the chain's hwupload/tonemap_opencl bind to.
            argumentBuilder.Append("-init_hw_device opencl=ocl -filter_hw_device ocl ");
        }

        // Input file
        argumentBuilder.Append($"-i \"{inputPath}\" ");

        // Timestamps and synchronization
        if (useFastSeek && seekPosition.HasValue && seekPosition.Value > 0)
        {
            // -copyts: Preserve timestamps for subtitle synchronization
            // -start_at_zero: Ensure the output HLS segments start their internal clock at zero
            argumentBuilder.Append("-copyts -start_at_zero ");
            _logger.LogInformation("Using -copyts to maintain subtitle sync for fast seek at {Seek}s", seekPosition.Value);
        }
        
        // Slow seek: -ss after -i
        if (!useFastSeek && seekPosition.HasValue && seekPosition.Value > 0)
        {
            argumentBuilder.Append($"-ss {seekPosition.Value:F2} ");
            _logger.LogInformation("Using slow seek for text subtitle synchronization at {Seek}s", seekPosition.Value);
        }


        
        string scaleFilter = "";
        string toneMapFilter = string.Empty;
        
        if (useToneMappingPipeline)
        {
             // min(W,iw) never upscales past the source (fake pixels waste CPU and bitrate —
             // the display's own upscaler does a better job); lanczos beats the default
             // bilinear/bicubic for the downscales that do happen.
             var targetWidth = TargetWidth(settings.MaxResolution);
             var scale = targetWidth > 0
                ? $"scale_cuda=w='min({targetWidth},iw)':h=-2:format=p010:interp_algo=lanczos"
                : "scale_cuda=format=p010";

            var chain = new List<string>();
            if (isInterlaced)
            {
                chain.Add("yadif_cuda=mode=send_frame"); // frames are in CUDA memory here
            }
            chain.Add(scale);

            var toneAlgo = NormalizeToneMapAlgorithm(settings.ToneMappingAlgorithm);
            chain.Add($"tonemap_cuda=tonemap={toneAlgo}:format=nv12");
            _logger.LogDebug("Using tonemap algorithm: {Algorithm}", toneAlgo);

            double fps = probe?.FrameRate > 0 ? probe.FrameRate : 24.0;
            chain.Add($"fps={fps}");

            if (hasSubtitleOverlay)
            {
               // Download from CUDA to CPU memory for subtitle burning
               chain.Add("hwdownload");
               chain.Add("format=nv12");
            }

            toneMapFilter = string.Join(",", chain);
        }
        else if (useOpenClToneMap)
        {
            // QS-WI-012: GPU-compute HDR→SDR for Intel/AMD via tonemap_opencl. Composition
            // mirrors the other two chains — deinterlace → scale → tone-map — so every
            // downstream branch (bitmap overlay, text burn-in, plain -vf) composes the same
            // way. The scale runs in software BEFORE the upload (fewer pixels through the
            // GPU hop), and the chain ends hwdownload,format=nv12 because the QSV/AMF
            // encoders consume system-memory frames — the same shape as the CUDA chain's
            // subtitle tail. Color targets (p/t/m=bt709) are explicit; desat=0 matches the
            // zscale/tonemap software chain's look (no extra highlight desaturation).
            var chain = new List<string>();
            if (isInterlaced)
            {
                chain.Add("bwdif=mode=send_frame");
            }

            var swScale = GetSoftwareScaleExpression(settings.MaxResolution);
            if (!string.IsNullOrEmpty(swScale))
            {
                chain.Add(swScale);
            }

            var toneAlgo = NormalizeToneMapAlgorithm(settings.ToneMappingAlgorithm);
            chain.Add($"format=p010le,hwupload,tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap={toneAlgo}:desat=0,hwdownload,format=nv12");
            _logger.LogDebug("Using OpenCL tonemap algorithm: {Algorithm}", toneAlgo);

            toneMapFilter = string.Join(",", chain);
        }
        else if (useSoftwareToneMap)
        {
            // SR-WI-023 #1/#2: software HDR→SDR chain for none/intel/amd. Composition mirrors the
            // CUDA pipeline's insertion point and ordering — deinterlace → scale → tone-map — so
            // every downstream branch (bitmap overlay, text burn-in, plain -vf) composes the same
            // way for both pipelines. Scaling BEFORE the tone map runs the expensive zscale
            // linearisation at the (usually smaller) target resolution, exactly like scale_cuda
            // feeding tonemap_cuda; scale quality on PQ-encoded pixels is not visually
            // distinguishable here and this matches the CUDA path's quality/CPU trade-off.
            // zscale is present in the bundled jellyfin-ffmpeg (verified 7.1.4).
            var chain = new List<string>();
            if (isInterlaced)
            {
                chain.Add("bwdif=mode=send_frame");
            }

            var swScale = GetSoftwareScaleExpression(settings.MaxResolution);
            if (!string.IsNullOrEmpty(swScale))
            {
                chain.Add(swScale);
            }

            var toneAlgo = NormalizeToneMapAlgorithm(settings.ToneMappingAlgorithm);
            chain.Add($"zscale=t=linear:npl=100,tonemap={toneAlgo},zscale=p=bt709:t=bt709:m=bt709:r=tv,format=yuv420p");
            _logger.LogDebug("Using software tonemap algorithm: {Algorithm}", toneAlgo);

            toneMapFilter = string.Join(",", chain);
        }

        if (!toneMapActive)
        {
            // Determine if we should preserve 10-bit depth (p010) for SDR content
            // Only if input is 10-bit and output codec supports it (HEVC/AV1)
            var c = settings.OutputVideoCodec.ToLower();
            bool codecSupports10Bit = c.Contains("av1") || c.Contains("hevc") || c == "libx265";
            bool shouldPreserve10Bit = is10Bit && codecSupports10Bit;

             // Subtitle branches deinterlace explicitly BEFORE the subtitles are drawn (below),
             // so only the no-subtitle path folds the deinterlacer into this filter.
             scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay, settings.HardwareAcceleration, shouldPreserve10Bit,
                 deinterlace: isInterlaced && !hasSubtitleOverlay);
             
             if (shouldPreserve10Bit && !string.IsNullOrEmpty(scaleFilter) && scaleFilter.Contains("p010"))
             {
                 _logger.LogInformation("Preserving 10-bit depth (p010) for 10-bit SDR content");
             }
        }

        // SR-WI-023 #4: explicit color metadata on EVERY encode output (previously none was
        // emitted, leaving players to guess). Tone-mapped or SDR output is tagged bt709; HDR
        // passthrough carries the source characteristics (HDR10/HDR10+ → PQ, HLG → arib-std-b67),
        // fixing the PreserveHDR fMP4 path that shipped HDR pixels without signaling.
        bool outputStaysHdr = isHdr && skipTonemapping && !forceToneMappingForSubtitles;
        string colorMetadataArgs;
        if (outputStaysHdr)
        {
            var trc = probe?.ColorTransfer?.Contains("arib-std-b67") == true ? "arib-std-b67" : "smpte2084";
            colorMetadataArgs = $"-color_primaries bt2020 -color_trc {trc} -colorspace bt2020nc ";
        }
        else
        {
            colorMetadataArgs = "-color_primaries bt709 -color_trc bt709 -colorspace bt709 ";
        }

        if (hasSubtitleOverlay)
        {
            var filterChain = new StringBuilder();
            
            if (IsBitmapSubtitleCodec(subtitleCodec))
            {
                // [0:s] is subtitles, [0:v] is video
                string videoInput = "[0:v]";

                if (toneMapActive)
                {
                    // Apply tone mapping to video first: [0:v]toneMapFilter[tm];
                    // (both tone-map chains already start with their deinterlacer for interlaced
                    // sources, and both already fold the scale in — no scaleFilter append below)
                    filterChain.Append($"[0:v]{toneMapFilter}[tm];");
                    videoInput = "[tm]";
                }
                else if (isInterlaced)
                {
                    // Deinterlace before scale2ref/overlay so subtitles land on progressive frames
                    filterChain.Append("[0:v]bwdif=mode=send_frame[dei];");
                    videoInput = "[dei]";
                }
                
                // Use scale2ref to ensure subtitles are scaled to match video
                // [0:s][videoInput]scale2ref...
                filterChain.Append($"[0:{subtitleTrackIndex}]{videoInput}scale2ref=flags=bicubic[subs][vid];");
                
                // [vid] is the video stream output from scale2ref (original resolution/tonemapped)
                string videoLabel = "[vid]";
                
                if (!toneMapActive && !string.IsNullOrEmpty(scaleFilter))
                {
                    // Cleanup scaleFilter if using software scaling
                    var cleanScale = scaleFilter.Replace("-vf ", "").Replace("\"", "").Trim();
                    if (cleanScale.StartsWith(",")) cleanScale = cleanScale.Substring(1);
                    
                    filterChain.Append($"{videoLabel}{cleanScale}[vscaled];");
                    videoLabel = "[vscaled]";
                }
                
                // Finally overlay subtitles onto video
                filterChain.Append($"{videoLabel}[subs]overlay");
                
                filterChain.Append("[v]");
                
                argumentBuilder.Append($"-filter_complex \"{filterChain}\" ");
                
                // Map processed video
                argumentBuilder.Append("-map \"[v]\" ");
                
                // Map audio
                if (audioTrackIndex.HasValue)
                {
                    argumentBuilder.Append($"-map 0:{audioTrackIndex.Value} ");
                }
                else
                {
                    argumentBuilder.Append("-map 0:a:0 ");
                }
            }
            else
            {
                // Text subtitles — burned from the pre-extracted session-local file (R-WI-012).
                // A bare relative filename resolves against ProcessStartInfo.WorkingDirectory
                // (= the session dir), so no path ever needs filter-level escaping, and the
                // extracted file has exactly one stream so no :si selector is needed.

                // Prepend tone mapping if active (its chain already deinterlaces and scales when
                // needed, and burning happens AFTER tone mapping so bt709 subtitle colors land on
                // bt709 frames — mirrors the CUDA chain)
                if (toneMapActive)
                {
                    filterChain.Append($"{toneMapFilter},");
                }
                else if (isInterlaced)
                {
                    // Deinterlace before burning subtitles so they land on progressive frames
                    filterChain.Append("bwdif=mode=send_frame,");
                }

                // fontsdir=. lets libass find the dumped embedded fonts (harmless when none exist).
                filterChain.Append($"subtitles={BurnInSubtitleFileName}:fontsdir=.");

                if (!toneMapActive && !string.IsNullOrEmpty(scaleFilter))
                {
                    filterChain.Append(scaleFilter);
                }

                argumentBuilder.Append($"-vf \"{filterChain}\" ");
            }
            
            argumentBuilder.Append(GetEncoderOptions(settings, probe?.FrameRate ?? 23.976, effectiveMaxBitrate));
            argumentBuilder.Append(colorMetadataArgs);
        }
        else
        {
             // No subtitles
             if (toneMapActive)
             {
                 argumentBuilder.Append($"-vf \"{toneMapFilter}\" ");
             }
             else if (!string.IsNullOrEmpty(scaleFilter))
             {
                 argumentBuilder.Append(scaleFilter);
             }
             
             argumentBuilder.Append(GetEncoderOptions(settings, probe?.FrameRate ?? 23.976, effectiveMaxBitrate));
             argumentBuilder.Append(colorMetadataArgs);
        }

        // Standard mapping for non-bitmap scenarios
        // (Bitmap scenarios handle mapping internally to preserve overlay)
        bool isBitmap = hasSubtitleOverlay && IsBitmapSubtitleCodec(subtitleCodec);

        // R-WI-004 (diff-review HIGH): PIN the audio stream, don't leave it to ffmpeg's implicit
        // selection. ffmpeg's default audio selection picks the stream with the MOST channels, but
        // the plan's copy/encode decision was made from the FIRST audio track (probe basis). On a
        // multi-track file (e.g. AC3 5.1 default + DTS-HD 7.1 alternate) implicit selection would
        // copy the undecodable alternate track — no audio, or a mux abort. Explicitly map 0:a:0
        // (first track) for the default case so `-c:a copy` copies exactly what the plan validated.
        bool hasAudio = !string.IsNullOrEmpty(probe?.AudioCodec) || (probe?.AudioChannels ?? 0) > 0;
        if (!isBitmap && (audioTrackIndex.HasValue || hasAudio))
        {
            argumentBuilder.Append("-map 0:v:0 ");
            argumentBuilder.Append(audioTrackIndex.HasValue ? $"-map 0:{audioTrackIndex.Value} " : "-map 0:a:0 ");
            _logger.LogInformation("Mapping video 0:v:0 and audio {Audio}",
                audioTrackIndex.HasValue ? $"0:{audioTrackIndex.Value}" : "0:a:0");
        }

        argumentBuilder.Append(BuildAudioArgs(audioCopy, audioCodec, audioChannels, audioTrackIndex, probe));
        argumentBuilder.Append("-start_at_zero ");
        
        var codecLower = settings.OutputVideoCodec.ToLower();
        bool useAv1 = codecLower == "av1" || codecLower.Contains("av1");
        bool useFmp4 = skipTonemapping || useAv1;
        
        var segmentExt = useFmp4 ? "m4s" : "ts";
        var segmentPath = Path.Combine(outputDir, $"{segmentPrefix}_%03d.{segmentExt}");
        
        argumentBuilder.Append("-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event ");
        
        if (useFmp4)
        {
            argumentBuilder.Append("-hls_segment_type fmp4 ");
            argumentBuilder.Append("-hls_fmp4_init_filename init.mp4 ");
            argumentBuilder.Append("-hls_flags independent_segments ");
            _logger.LogInformation("Using fMP4 segments (reason: {Reason}, codec={Codec})", 
                useAv1 ? "AV1 requires fMP4" : "HDR passthrough", codecLower);
        }
        else
        {
            argumentBuilder.Append("-hls_flags append_list ");
        }
        
        argumentBuilder.Append($"-start_number 0 -hls_segment_filename \"{segmentPath}\" ");
        argumentBuilder.Append($"\"{playlistPath}\"");

        var arguments = argumentBuilder.ToString();
        var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
        
        _logger.LogInformation("FFmpeg command: {Path} {Args}", ffmpegPath, arguments);
        _logger.LogInformation("Transcode settings: HW={HW}, Preset={Preset}, CRF={CRF}, Threads={Threads}, Resolution={Res}, Codec={Codec}", 
            settings.HardwareAcceleration, settings.Preset, settings.CRF, settings.ThreadCount, settings.MaxResolution, settings.OutputVideoCodec);

        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = outputDir
        };
    }

    /// <summary>
    /// REMUX path (R-WI-003): stream-copy the compatible video + audio into fMP4 HLS segments.
    /// fMP4 (not the default MPEG-TS) is required because the prime remux case is HEVC-in-MKV for
    /// an HEVC-capable client, and copied HEVC does not play in TS on the Safari/hls.js clients
    /// that advertise it; the fMP4 path already emits an init.mp4 the client fetches. No decode,
    /// no encode, no filters — CPU stays near idle. Subtitles ride as sidecar VTT (delivered by the
    /// separate subtitles.vtt endpoint), exactly as on the transcode path.
    /// </summary>
    public ProcessStartInfo BuildRemuxArguments(
        string inputPath,
        string outputDir,
        string segmentPrefix,
        double? seekPosition = null,
        int? audioTrackIndex = null)
    {
        Directory.CreateDirectory(outputDir);
        var playlistPath = Path.Combine(outputDir, "master.m3u8");
        var sb = new StringBuilder();

        // Fast (keyframe) seek before -i is correct and cheap for stream copy.
        if (seekPosition is > 0)
            sb.Append($"-ss {seekPosition.Value:F2} ");

        sb.Append($"-i \"{inputPath}\" ");

        if (seekPosition is > 0)
            sb.Append("-copyts -start_at_zero ");

        // Map the video + the selected (or default) audio track and copy both — no re-encode.
        sb.Append("-map 0:v:0 ");
        sb.Append(audioTrackIndex.HasValue ? $"-map 0:{audioTrackIndex.Value} " : "-map 0:a:0 ");
        sb.Append("-c copy ");

        // fMP4 segments (see summary). Mirrors the transcode builder's fMP4 branch.
        var segmentPath = Path.Combine(outputDir, $"{segmentPrefix}_%03d.m4s");
        sb.Append("-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event ");
        sb.Append("-hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_flags independent_segments ");
        sb.Append($"-start_number 0 -hls_segment_filename \"{segmentPath}\" ");
        sb.Append($"\"{playlistPath}\"");

        var arguments = sb.ToString();
        var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
        _logger.LogInformation("FFmpeg REMUX (stream-copy) command: {Path} {Args}", ffmpegPath, arguments);

        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = outputDir
        };
    }

    /// <summary>
    /// R-WI-004 audio emission for the transcode path (replaces the old forced <c>-c:a aac -ac 2
    /// -b:a 128k</c>). The plan decided the ladder — copy the source audio (preserving surround),
    /// else encode to the target codec/channels. Copy is applied only to the DEFAULT audio track:
    /// the plan's copy decision was made from the source's PRIMARY audio codec, which may differ
    /// from an explicitly selected non-default track, so a selected track always encodes (which is
    /// container-safe) rather than risk copying an incompatible codec.
    /// </summary>
    private static string BuildAudioArgs(bool audioCopy, string? audioCodec, int audioChannels, int? audioTrackIndex,
        MediaProbeResult? probe = null)
    {
        if (audioCopy && audioTrackIndex == null)
            return "-c:a copy ";

        // An explicitly selected non-default track: the plan's codec/channels were negotiated for
        // the DEFAULT track, so don't impose that exact layout (it could UPMIX this track —
        // diff-review LOW). But the client's channel CEILING still applies: this branch used to
        // omit -ac entirely, so a 6-channel TrueHD track selected by a stereo-only browser was
        // encoded as 6-channel AAC with an unknown channel layout. Chrome cannot initialise a
        // decoder for it — every SourceBuffer append raised an error and hls.js recreated the
        // buffer forever, so the movie fetched segments but NEVER played (live-diagnosed:
        // 3215 SourceBuffer recreations, buffered=[], readyState=0).
        // Cap at the ceiling, never upmix: min(source channels, client ceiling).
        if (audioTrackIndex != null)
        {
            // Resolve THIS track's channel count. `audioTrackIndex` is the ABSOLUTE stream index
            // (what the tracks endpoint hands the client and what `-map 0:N` consumes), so match
            // StreamIndex first; the audio-relative Index is only a fallback for probes predating
            // StreamIndex. Falling back to the PRIMARY track's count would be wrong on a
            // multi-track file (a stereo alternate beside a 5.1 default would get upmixed), so
            // when the track can't be resolved, cap at the ceiling without claiming to know better.
            var track = probe?.AudioTracks?.FirstOrDefault(t => t.StreamIndex == audioTrackIndex.Value)
                        ?? probe?.AudioTracks?.FirstOrDefault(t => t.Index == audioTrackIndex.Value);
            // Unresolvable track (probe without a track list): fall back to the PRIMARY track's
            // count. Erring low is safe — too few channels is a quality nit, too many is an
            // undecodable stream. The multi-track mis-resolution this fallback used to cause is
            // now prevented by the StreamIndex match above.
            var sourceChannels = track?.Channels ?? probe?.AudioChannels ?? 0;
            var ceiling = audioChannels > 0 ? audioChannels : 2;
            var selectedChannels = sourceChannels > 0 ? Math.Min(sourceChannels, ceiling) : ceiling;
            return $"-c:a aac -ac {selectedChannels} -b:a {(selectedChannels >= 6 ? 384 : 256)}k ";
        }

        var codec = audioCodec is "aac" or "ac3" or "eac3" ? audioCodec : "aac";
        var channels = audioChannels > 0 ? audioChannels : 2;

        // Bitrate scaled to channel count; AC3/EAC3 need more headroom than AAC.
        var bitrateK = codec == "aac"
            ? (channels >= 6 ? 384 : 128)
            : (channels >= 6 ? 448 : 192); // ac3 / eac3
        return $"-c:a {codec} -ac {channels} -b:a {bitrateK}k ";
    }

    // --- Helpers ---

    private bool Is10BitOrHdr(string? pixelFormat, string? colorTransfer)
    {
        if (string.IsNullOrEmpty(pixelFormat)) return false;
        return pixelFormat.Contains("p10") || 
               pixelFormat.Contains("p010") ||
               pixelFormat.Contains("10le") || 
               (colorTransfer != null && (colorTransfer.Contains("smpte2084") || colorTransfer.Contains("arib-std-b67")));
    }

    private bool IsHdr(string? colorTransfer)
    {
        return colorTransfer != null && (colorTransfer.Contains("smpte2084") || colorTransfer.Contains("arib-std-b67"));
    }

    /// <summary>
    /// SR-WI-023 #5: true when the negotiated output codec can carry 10-bit HDR (hevc/av1).
    /// h264 (and "auto", which resolves to h264 in <see cref="GetVideoEncoder"/>) cannot —
    /// PreserveHDR must be overridden to tone mapping for those.
    /// </summary>
    public static bool CodecCanCarryHdr(string outputVideoCodec)
    {
        var c = outputVideoCodec.ToLowerInvariant();
        return c.Contains("hevc") || c.Contains("265") || c.Contains("av1");
    }

    /// <summary>
    /// QS-WI-006 — the transcode ladder: default CVBR ceilings (kbps, h264) per output
    /// height, applied ONLY when no bitrate ceiling was negotiated for the session.
    /// Audited 2026-08-01 against the Apple HLS authoring guidelines and current
    /// Jellyfin/Plex community guidance; values sit ~20% above typical steady-state
    /// recommendations so CRF remains the quality driver and the ceiling only trims spikes:
    ///
    ///   height | h264 kbps | hevc/av1 kbps (×0.6)
    ///   -------|-----------|---------------------
    ///     480  |   2 500   |  1 500
    ///     720  |   5 000   |  3 000
    ///    1080  |   9 000   |  5 400
    ///    1440  |  14 000   |  8 400
    ///    2160  |  22 000   | 13 200
    ///
    /// Not admin-tunable by design (no new knobs) — quality/speed are steered via the
    /// existing TranscodeCRF/TranscodePreset settings, and any negotiated cap (server,
    /// network, per-user, or client ask) always replaces these defaults outright.
    /// </summary>
    private static readonly (int Height, int H264Kbps)[] DefaultLadder =
    {
        (480, 2_500),
        (720, 5_000),
        (1080, 9_000),
        (1440, 14_000),
        (2160, 22_000),
    };

    /// <summary>
    /// Resolve the QS-WI-006 ladder default for this session's output. Returns 0 (no cap)
    /// when the output height can't be determined — never guess low. The never-upscale
    /// clamp applies: with MaxResolution=original (or a target above the source), the
    /// SOURCE height picks the rung.
    /// </summary>
    private static int GetDefaultLadderMaxRateKbps(string maxResolution, int sourceHeight, string outputVideoCodec)
    {
        var targetHeight = maxResolution.ToLowerInvariant() switch
        {
            "480p" => 480,
            "720p" => 720,
            "1080p" => 1080,
            "1440p" => 1440,
            "4k" or "2160p" => 2160,
            "8k" or "4320p" => 4320, // above the top rung: the 2160 ceiling applies
            _ => 0, // "original"/unknown: bounded only by the source
        };
        if (targetHeight <= 0) targetHeight = sourceHeight;
        else if (sourceHeight > 0) targetHeight = Math.Min(targetHeight, sourceHeight);
        if (targetHeight <= 0) return 0;

        var h264Kbps = DefaultLadder[^1].H264Kbps;
        foreach (var (height, kbps) in DefaultLadder)
        {
            if (targetHeight <= height)
            {
                h264Kbps = kbps;
                break;
            }
        }

        // hevc/av1 reach the same quality at roughly 60% of the h264 rate.
        var codec = outputVideoCodec.ToLowerInvariant();
        var efficientCodec = codec.Contains("hevc") || codec.Contains("265") || codec.Contains("av1");
        return efficientCodec ? (int)(h264Kbps * 0.6) : h264Kbps;
    }

    private static int ParseHeightFromResolution(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution)) return 0;
        var parts = resolution.Split('x');
        return parts.Length == 2 && int.TryParse(parts[1], out var height) ? height : 0;
    }

    /// <summary>Validated tone-map operator; anything unknown falls back to hable.</summary>
    private static string NormalizeToneMapAlgorithm(string algorithm)
    {
        var toneAlgo = algorithm.ToLowerInvariant();
        return toneAlgo is "hable" or "reinhard" or "mobius" ? toneAlgo : "hable";
    }

    /// <summary>
    /// Target WIDTH for a quality label; 0 = no scaling ("original"/unknown). The single
    /// label→width map behind every scale filter — the plan encodes numeric heights as
    /// "{n}p" strings (e.g. "1440p", "2160p"), so all of those must resolve here, not just
    /// the admin-setting labels (720p/1080p/4k).
    /// </summary>
    private static int TargetWidth(string maxResolution) => maxResolution.ToLowerInvariant() switch
    {
        "480p" => 854,
        "720p" => 1280,
        "1080p" => 1920,
        "1440p" => 2560,
        "4k" or "2160p" => 3840,
        "8k" or "4320p" => 7680,
        _ => 0,
    };

    /// <summary>
    /// Bare software scale expression (no -vf wrapper) with the never-upscale clamp and lanczos —
    /// shared by <see cref="GetScaleFilter"/> and the software/OpenCL tone-map chains. Empty for
    /// "original" (no scaling).
    /// </summary>
    private static string GetSoftwareScaleExpression(string maxResolution)
    {
        var width = TargetWidth(maxResolution);
        return width > 0 ? $"scale='min({width},iw)':-2:flags=lanczos" : "";
    }

    private string GetHardwareDecodeOptions(string hwAccel, bool hasSubtitleOverlay, bool useToneMappingPipeline)
    {
        return hwAccel.ToLower() switch
        {
            "nvidia" => (useToneMappingPipeline || !hasSubtitleOverlay) 
                ? "-hwaccel cuda -hwaccel_output_format cuda "
                : "-hwaccel cuda ", // Software output for legacy subtitle path if not tone mapping
            "intel" => "-hwaccel qsv -init_hw_device qsv=hw -filter_hw_device hw ",
            "amd" => "-hwaccel d3d11va ",
            _ => ""
        };
    }

    private string GetEncoderOptions(TranscodeSettings settings, double fps, int? maxBitrate)
    {
        var encoder = GetVideoEncoder(settings.HardwareAcceleration, settings.OutputVideoCodec);
        _logger.LogDebug("Selected encoder: {Encoder} for codec: {Codec}, hw: {HW}, fps: {FPS}, maxRate: {Bitrate}", 
            encoder, settings.OutputVideoCodec, settings.HardwareAcceleration, fps, maxBitrate);

        // Calculate GOP size for consistent 6s segments
        // We use -hls_time 6, so keyframes should be every 6 seconds
        var gopSize = (int)Math.Round(fps * 6.0);
        var keyframeFlags = $"-g {gopSize} -keyint_min {gopSize} -sc_threshold 0 -force_key_frames \"expr:gte(t,n_forced*6)\" ";
        
        // Bitrate control arguments
        var bitrateArgs = "";
        if (maxBitrate.HasValue && maxBitrate.Value > 0)
        {
            // Set maxrate and bufsize for Constrained VBR (CVBR)
            // Buffer size = 2x maxrate is a common recommendation for HLS to handle variability
            bitrateArgs = $"-maxrate {maxBitrate.Value}k -bufsize {maxBitrate.Value * 2}k ";
        }

        if (encoder == "libx264")
        {
            return $"-c:v libx264 -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                   $"-preset {settings.Preset} -crf {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "h264_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            return $"-c:v h264_nvenc -preset {nvencPreset} -cq {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "h264_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            // AMF might handle maxrate differently, but typically respects standard ffmpeg flags or needs separate -rc options
            // With standard ffmpeg, -maxrate usually works. 
            // If explicit RC mode is needed, we stick to cqp unless bitrate is set, then maybe vbr_latency?
            // For now, appending bitrateArgs attempts to layer it on top.
            return $"-c:v h264_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} -pix_fmt yuv420p {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "h264_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v h264_qsv -preset {qsvPreset} -global_quality {settings.CRF} -pix_fmt nv12 {bitrateArgs}{keyframeFlags}";
        }
        // HEVC encoders
        else if (encoder == "libx265")
        {
            var adjustedCrf = Math.Min(settings.CRF + 2, 51);
            return $"-c:v libx265 -preset {settings.Preset} -crf {adjustedCrf} -pix_fmt yuv420p {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "hevc_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            return $"-c:v hevc_nvenc -preset {nvencPreset} -cq {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "hevc_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            return $"-c:v hevc_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "hevc_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v hevc_qsv -preset {qsvPreset} -global_quality {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        // AV1 encoders
        else if (encoder == "av1_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            var adjustedCrf = Math.Min(settings.CRF + 4, 63);
            return $"-c:v av1_nvenc -preset {nvencPreset} -cq {adjustedCrf} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "av1_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            return $"-c:v av1_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        else if (encoder == "av1_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v av1_qsv -preset {qsvPreset} -global_quality {settings.CRF} {bitrateArgs}{keyframeFlags}";
        }
        
        return $"-c:v libx264 -preset {settings.Preset} -crf {settings.CRF} -pix_fmt yuv420p {bitrateArgs}{keyframeFlags}";
    }

    /// <summary>
    /// Scale (and optionally deinterlace) filter. `min(W,iw)` clamps every target to the source
    /// width so the transcoder NEVER upscales (fake pixels waste CPU/bitrate and look worse than
    /// the display's own upscaler); lanczos sharpens the real downscales. `deinterlace` is only
    /// honored on the no-subtitle paths — subtitle branches insert their own deinterlacer ahead
    /// of the subtitle draw.
    /// </summary>
    private string GetScaleFilter(string maxResolution, bool hasSubtitleOverlay, string hwAccel, bool preserve10Bit = false, bool deinterlace = false)
    {
        if (hwAccel.ToLower() == "nvidia" && !hasSubtitleOverlay)
        {
            string format = preserve10Bit ? "p010" : "nv12";
            var targetWidth = TargetWidth(maxResolution);
            var scaleCuda = targetWidth > 0
                ? $"scale_cuda=w='min({targetWidth},iw)':h=-2:format={format}:interp_algo=lanczos"
                : $"scale_cuda=format={format}";

            // Frames are in CUDA memory on this path (see GetHardwareDecodeOptions)
            var cudaChain = deinterlace ? $"yadif_cuda=mode=send_frame,{scaleCuda}" : scaleCuda;
            return $"-vf \"{cudaChain}\" ";
        }

        var scale = GetSoftwareScaleExpression(maxResolution);

        var parts = new List<string>();
        if (deinterlace) parts.Add("bwdif=mode=send_frame");
        if (!string.IsNullOrEmpty(scale)) parts.Add(scale);
        if (parts.Count == 0) return "";
        var chain = string.Join(",", parts);

        if (hasSubtitleOverlay)
        {
            return $",{chain}";
        }
        return $"-vf \"{chain}\" ";
    }

    private bool IsBitmapSubtitleCodec(string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return false;
        
        var bitmapCodecs = new[] 
        { 
            "hdmv_pgs_subtitle", "pgs", 
            "dvd_subtitle", "dvdsub", 
            "xsub",
            "dvb_subtitle"
        };
        
        return bitmapCodecs.Contains(codec.ToLowerInvariant());
    }

    private string GetVideoEncoder(string hwAccel, string targetCodec = "h264")
    {
        var hw = hwAccel.ToLower();
        var codec = targetCodec.ToLower();
        
        if (codec == "auto") codec = "h264";
        
        return (codec, hw) switch
        {
            ("av1", "nvidia") => "av1_nvenc",
            ("av1", "amd") => "av1_amf",
            ("av1", "intel") => "av1_qsv",
            ("av1", _) => GetVideoEncoder(hwAccel, "hevc"),
            
            ("hevc", "nvidia") => "hevc_nvenc",
            ("hevc", "amd") => "hevc_amf",
            ("hevc", "intel") => "hevc_qsv",
            ("hevc", _) => "libx265",
            
            ("h264", "nvidia") => "h264_nvenc",
            ("h264", "amd") => "h264_amf",
            ("h264", "intel") => "h264_qsv",
            ("h264", _) => "libx264",
            
            _ => "libx264"
        };
    }

    private static string MapToNvencPreset(string preset) => preset switch
    {
        "ultrafast" or "superfast" => "p1",
        "veryfast" or "faster" => "p2",
        "fast" => "p3",
        "medium" => "p4",
        "slow" => "p5",
        "slower" => "p6",
        "veryslow" => "p7",
        _ => "p2"
    };

    private static string MapToAmfQuality(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" => "speed",
        "faster" or "fast" or "medium" => "balanced",
        _ => "quality"
    };

    private static string MapToQsvPreset(string preset) => preset switch
    {
        "ultrafast" or "superfast" => "veryfast",
        "veryslow" => "veryslow",
        _ => preset
    };
}
