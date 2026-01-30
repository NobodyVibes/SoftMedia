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
        int? maxBitrate = null);
}

public class TranscodeProfileBuilder : ITranscodeProfileBuilder
{
    private readonly ILogger<TranscodeProfileBuilder> _logger;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ISubtitleService _subtitleService;

    public TranscodeProfileBuilder(
        ILogger<TranscodeProfileBuilder> logger,
        IBinaryLocationService binaryLocationService,
        IMediaProbeService mediaProbeService,
        ISubtitleService subtitleService)
    {
        _logger = logger;
        _binaryLocationService = binaryLocationService;
        _mediaProbeService = mediaProbeService;
        _subtitleService = subtitleService;
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
        int? maxBitrate = null)
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

        // Determine subtitle codec type FIRST (needed to decide seek strategy)
        bool hasSubtitleOverlay = subtitleTrackIndex.HasValue;
        string? subtitleCodec = null;
        bool useTextSubtitles = false;
        
        if (hasSubtitleOverlay)
        {
            subtitleCodec = await _mediaProbeService.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex!.Value);
            _logger.LogInformation("Subtitle track {Index} codec: {Codec}", subtitleTrackIndex, subtitleCodec ?? "unknown");
            useTextSubtitles = !IsBitmapSubtitleCodec(subtitleCodec);
            
            // WORKAROUND: FFmpeg's subtitles filter on Windows cannot handle apostrophes in paths
            if (useTextSubtitles && inputPath.Contains("'"))
            {
                _logger.LogWarning("Skipping text subtitle burn-in for file with apostrophe in path: {Path}", inputPath);
                hasSubtitleOverlay = false;
                useTextSubtitles = false;
            }
        }

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
        bool skipTonemapping = settings.PreserveHDR && isHdr;
        
        // Smart HDR Override: If subtitles are active on HDR content, we MUST tone map to burn them in accurately.
        bool forceToneMappingForSubtitles = isHdr && hasSubtitleOverlay;
        bool useToneMappingPipeline = isHdr && settings.HardwareAcceleration.ToLower() == "nvidia" && (!skipTonemapping || forceToneMappingForSubtitles);
        
        if (skipTonemapping && !forceToneMappingForSubtitles)
        {
            _logger.LogInformation("PreserveHDR enabled: skipping tonemapping for 10-bit/HDR content");
        }
        else if (skipTonemapping && forceToneMappingForSubtitles)
        {
            _logger.LogInformation("PreserveHDR enabled but OVERRIDDEN: tone mapping forced for subtitle burn-in compatibility");
        }

        // HARDWARE DECODE
        var hwDecodeOptions = GetHardwareDecodeOptions(settings.HardwareAcceleration, hasSubtitleOverlay, useToneMappingPipeline);
        if (!string.IsNullOrEmpty(hwDecodeOptions))
        {
            argumentBuilder.Append(hwDecodeOptions);
            _logger.LogInformation("Using hardware decode: {HwDecode}", hwDecodeOptions.Trim());
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
             var scale = settings.MaxResolution.ToLower() switch
            {
                "720p" => "scale_cuda=1280:-2:format=p010", 
                "1080p" => "scale_cuda=1920:-2:format=p010",
                "4k" => "scale_cuda=3840:-2:format=p010",
                _ => "scale_cuda=format=p010" 
            };
            
            var chain = new List<string>();
            chain.Add(scale);
            
            var toneAlgo = settings.ToneMappingAlgorithm.ToLower();
            if (toneAlgo != "hable" && toneAlgo != "reinhard" && toneAlgo != "mobius")
            {
                toneAlgo = "hable";
            }
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
        
        if (!useToneMappingPipeline)
        {
            // Determine if we should preserve 10-bit depth (p010) for SDR content
            // Only if input is 10-bit and output codec supports it (HEVC/AV1)
            var c = settings.OutputVideoCodec.ToLower();
            bool codecSupports10Bit = c.Contains("av1") || c.Contains("hevc") || c == "libx265";
            bool shouldPreserve10Bit = is10Bit && codecSupports10Bit;

             scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay, settings.HardwareAcceleration, shouldPreserve10Bit);
             
             if (shouldPreserve10Bit && !string.IsNullOrEmpty(scaleFilter) && scaleFilter.Contains("p010"))
             {
                 _logger.LogInformation("Preserving 10-bit depth (p010) for 10-bit SDR content");
             }
        }
        
        if (hasSubtitleOverlay)
        {
            var filterChain = new StringBuilder();
            
            if (IsBitmapSubtitleCodec(subtitleCodec))
            {
                // [0:s] is subtitles, [0:v] is video
                string videoInput = "[0:v]";
                
                if (useToneMappingPipeline)
                {
                    // Apply tone mapping to video first: [0:v]toneMapFilter[tm];
                    filterChain.Append($"[0:v]{toneMapFilter}[tm];");
                    videoInput = "[tm]";
                }
                
                // Use scale2ref to ensure subtitles are scaled to match video
                // [0:s][videoInput]scale2ref...
                filterChain.Append($"[0:{subtitleTrackIndex}]{videoInput}scale2ref=flags=bicubic[subs][vid];");
                
                // [vid] is the video stream output from scale2ref (original resolution/tonemapped)
                string videoLabel = "[vid]";
                
                if (!useToneMappingPipeline && !string.IsNullOrEmpty(scaleFilter))
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
                // Text subtitles
                var escapedPath = inputPath
                    .Replace("\\", "/")
                    .Replace(":", "\\:")
                    .Replace("'", @"\\'");
                
                var si = await _subtitleService.GetSubtitleStreamIndexAsync(inputPath, subtitleTrackIndex ?? 0);
                
                // Prepend tone mapping if active
                if (useToneMappingPipeline)
                {
                    filterChain.Append($"{toneMapFilter},");
                }
                
                filterChain.Append($"subtitles='{escapedPath}':si={si}");
                
                if (!useToneMappingPipeline && !string.IsNullOrEmpty(scaleFilter))
                {
                    filterChain.Append(scaleFilter);
                }
                
                argumentBuilder.Append($"-vf \"{filterChain}\" ");
            }
            
            argumentBuilder.Append(GetEncoderOptions(settings, probe?.FrameRate ?? 23.976, maxBitrate));
        }
        else 
        {
             // No subtitles
             if (useToneMappingPipeline)
             {
                 argumentBuilder.Append($"-vf \"{toneMapFilter}\" ");
             }
             else if (!string.IsNullOrEmpty(scaleFilter))
             {
                 argumentBuilder.Append(scaleFilter);
             }
             
             argumentBuilder.Append(GetEncoderOptions(settings, probe?.FrameRate ?? 23.976, maxBitrate));
        }
        
        // Standard mapping for non-bitmap scenarios
        // (Bitmap scenarios handle mapping internally to preserve overlay)
        bool isBitmap = hasSubtitleOverlay && IsBitmapSubtitleCodec(subtitleCodec);
        
        if (audioTrackIndex.HasValue && !isBitmap)
        {
            argumentBuilder.Append("-map 0:v:0 ");
            argumentBuilder.Append($"-map 0:{audioTrackIndex.Value} ");
            _logger.LogInformation("Mapping video stream 0:v:0 and audio stream 0:{Index}", audioTrackIndex.Value);
        }
        
        argumentBuilder.Append("-c:a aac -ac 2 -b:a 128k ");
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

    private string GetScaleFilter(string maxResolution, bool hasSubtitleOverlay, string hwAccel, bool preserve10Bit = false)
    {
        if (hwAccel.ToLower() == "nvidia" && !hasSubtitleOverlay)
        {
            string format = preserve10Bit ? "p010" : "nv12";
            var scaleCuda = maxResolution.ToLower() switch
            {
                "720p" => $"scale_cuda=1280:-2:format={format}",
                "1080p" => $"scale_cuda=1920:-2:format={format}",
                "4k" => $"scale_cuda=3840:-2:format={format}",
                _ => $"scale_cuda=format={format}" 
            };
            
            return $"-vf \"{scaleCuda}\" ";
        }

        var scale = maxResolution.ToLower() switch
        {
            "720p" => "scale=1280:-2",
            "1080p" => "scale=1920:-2",
            "4k" => "scale=3840:-2",
            _ => "" 
        };
        
        if (string.IsNullOrEmpty(scale)) return "";
        
        if (hasSubtitleOverlay)
        {
            return $",{scale}";
        }
        return $"-vf \"{scale}\" ";
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
