using System.Diagnostics;
using System.Text.Json;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

public interface IFFmpegService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
    ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix);
    /// <summary>
    /// Get transcode arguments with optional subtitle burn-in and seek position.
    /// </summary>
    ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition);
    /// <summary>
    /// Get transcode arguments with subtitle, seek, read rate, target resolution, codec, HDR, and audio track settings.
    /// </summary>
    ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate, string? targetResolution = null, string? targetCodec = null, bool preserveHdr = false, int? audioTrackIndex = null);
    
    /// <summary>
    /// Extract a subtitle track to WebVTT format for HLS sidecar delivery.
    /// </summary>
    /// <param name="inputPath">Path to the input video file</param>
    /// <param name="subtitleStreamIndex">Index of the subtitle stream in FFmpeg notation (0-based among subtitle streams)</param>
    /// <param name="outputPath">Path where the WebVTT file will be written</param>
    /// <returns>True if extraction succeeded, false otherwise</returns>
    Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath);
    
    /// <summary>
    /// Convert an absolute stream index to a subtitle-relative index.
    /// Needed because FFmpeg's -map 0:s:N uses subtitle-relative indexing.
    /// </summary>
    int GetSubtitleStreamIndex(string inputPath, int absoluteStreamIndex);
    
    /// <summary>
    /// Offset all timestamps in a WebVTT file by subtracting the given offset.
    /// This is needed when seeking - the video starts at seek position but plays from time 0.
    /// </summary>
    void OffsetWebVttTimestamps(string vttPath, double offsetSeconds);
    
    /// <summary>
    /// Probe a file to get the subtitle codec for a specific track.
    /// </summary>
    Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex);
    
    /// <summary>
    /// Probe a file to get the subtitle language for a specific track.
    /// </summary>
    Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex);

    /// <summary>
    /// Check if transcoding is disabled in settings.
    /// </summary>
    Task<bool> IsTranscodingDisabledAsync();
}

public class MediaProbeResult
{
    public double Duration { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public double? CreditsStart { get; set; }  // Start time of credits chapter (if found)
    public string? PixelFormat { get; set; }
    public string? ColorTransfer { get; set; }
    public double FrameRate { get; set; }
    public List<(double StartTime, string Title)>? Chapters { get; set; }  // All chapters
}

/// <summary>
/// Cached transcoding settings loaded from the database.
/// </summary>
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

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly ISettingsService _settingsService;

    public FFmpegService(ILogger<FFmpegService> logger, IProcessRunner processRunner, ISettingsService settingsService)
    {
        _logger = logger;
        _processRunner = processRunner;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Load transcoding settings from the database.
    /// </summary>
    private async Task<TranscodeSettings> LoadSettingsAsync()
    {
        var enableStr = await _settingsService.GetSettingAsync("EnableTranscoding", "true");
        var hwAccel = await _settingsService.GetSettingAsync("HardwareAcceleration", "none");
        var preset = await _settingsService.GetSettingAsync("TranscodePreset", "veryfast");
        var threadCountStr = await _settingsService.GetSettingAsync("TranscodeThreadCount", "0");
        var maxRes = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
        var crfStr = await _settingsService.GetSettingAsync("TranscodeCRF", "23");
        var outputCodec = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
        var toneMapAlgo = await _settingsService.GetSettingAsync("ToneMappingAlgorithm", "hable");
        var preserveHdrStr = await _settingsService.GetSettingAsync("PreserveHDR", "false");
        
        return new TranscodeSettings
        {
            EnableTranscoding = bool.TryParse(enableStr, out var enable) && enable,
            HardwareAcceleration = hwAccel,
            Preset = preset,
            ThreadCount = int.TryParse(threadCountStr, out var tc) ? tc : 0,
            MaxResolution = maxRes,
            CRF = int.TryParse(crfStr, out var crf) ? crf : 23,
            OutputVideoCodec = outputCodec,
            ToneMappingAlgorithm = toneMapAlgo,
            PreserveHDR = bool.TryParse(preserveHdrStr, out var pHdr) && pHdr
        };
    }

    /// <summary>
    /// Check if transcoding is disabled in settings.
    /// </summary>
    public async Task<bool> IsTranscodingDisabledAsync()
    {
        var value = await _settingsService.GetSettingAsync("EnableTranscoding", "true");
        return bool.TryParse(value, out var enabled) && !enabled;
    }

    /// <summary>
    /// Get the video encoder based on hardware acceleration and target codec.
    /// Implements fallback chain: av1 → hevc → h264 (software always available for h264/hevc)
    /// </summary>
    /// <param name="hwAccel">Hardware acceleration setting (nvidia, amd, intel, none)</param>
    /// <param name="targetCodec">Target codec (auto, h264, hevc, av1). AV1 is hardware-only.</param>
    /// <returns>FFmpeg encoder name</returns>
    private string GetVideoEncoder(string hwAccel, string targetCodec = "h264")
    {
        var hw = hwAccel.ToLower();
        var codec = targetCodec.ToLower();
        
        // For "auto", default to h264 for maximum compatibility
        if (codec == "auto") codec = "h264";
        
        return (codec, hw) switch
        {
            // AV1: Hardware only (no software fallback - too slow)
            ("av1", "nvidia") => "av1_nvenc",
            ("av1", "amd") => "av1_amf",
            ("av1", "intel") => "av1_qsv",
            // AV1 fallback to HEVC
            ("av1", _) => GetVideoEncoder(hwAccel, "hevc"),
            
            // HEVC: Hardware preferred, software fallback available
            ("hevc", "nvidia") => "hevc_nvenc",
            ("hevc", "amd") => "hevc_amf",
            ("hevc", "intel") => "hevc_qsv",
            ("hevc", _) => "libx265",
            
            // H264: Universal support
            ("h264", "nvidia") => "h264_nvenc",
            ("h264", "amd") => "h264_amf",
            ("h264", "intel") => "h264_qsv",
            ("h264", _) => "libx264",
            
            // Default fallback
            _ => "libx264"
        };
    }

    /// <summary>
    /// Get hardware decode options to be placed BEFORE the input file.
    /// This enables GPU-accelerated decoding for full hardware transcoding pipeline.
    /// </summary>
    /// <param name="hwAccel">Hardware acceleration setting (nvidia, amd, intel, none)</param>
    /// <param name="hasSubtitleOverlay">Whether subtitle burn-in is needed (may require CPU processing)</param>
    /// <returns>FFmpeg hardware decode arguments, or empty if software decode should be used</returns>
    private string GetHardwareDecodeOptions(string hwAccel, bool hasSubtitleOverlay)
    {
        // Note: When subtitle burn-in is used, we may need to download frames to CPU
        // for text rendering, reducing hardware acceleration benefits.
        // However, we still use hardware decode as it's faster than software decode.
        
        return hwAccel.ToLower() switch
        {
            // NVIDIA: Use CUDA for decode, keep frames in GPU memory
            // -hwaccel cuda: Use NVIDIA GPU for decoding
            // -hwaccel_output_format cuda: Keep decoded frames in GPU memory (for h264_nvenc)
            "nvidia" => "-hwaccel cuda -hwaccel_output_format cuda ",
            
            // Intel QuickSync: Use QSV for decode
            // -hwaccel qsv: Use Intel GPU for decoding
            // -init_hw_device qsv=hw: Initialize QSV hardware device
            // -filter_hw_device hw: Use this device for hardware filters
            "intel" => "-hwaccel qsv -init_hw_device qsv=hw -filter_hw_device hw ",
            
            // AMD: Use D3D11VA for decode on Windows
            // -hwaccel d3d11va: Use DirectX 11 Video Acceleration
            // Note: In multi-GPU systems (e.g., NVIDIA + AMD iGPU), D3D11VA may bind to wrong GPU.
            // For most users with discrete AMD GPUs, this works correctly.
            "amd" => "-hwaccel d3d11va ",
            
            // No hardware acceleration - use software decode
            _ => ""
        };
    }

    /// <summary>
    /// Get encoder-specific options based on hardware/software selection and target codec.
    /// </summary>
    private string GetEncoderOptions(TranscodeSettings settings)
    {
        var encoder = GetVideoEncoder(settings.HardwareAcceleration, settings.OutputVideoCodec);
        _logger.LogDebug("Selected encoder: {Encoder} for codec: {Codec}, hw: {HW}", 
            encoder, settings.OutputVideoCodec, settings.HardwareAcceleration);
        
        // H.264 encoders
        if (encoder == "libx264")
        {
            return $"-c:v libx264 -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                   $"-preset {settings.Preset} -crf {settings.CRF} ";
        }
        else if (encoder == "h264_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            return $"-c:v h264_nvenc -preset {nvencPreset} -cq {settings.CRF} ";
        }
        else if (encoder == "h264_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            return $"-c:v h264_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} -pix_fmt yuv420p ";
        }
        else if (encoder == "h264_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v h264_qsv -preset {qsvPreset} -global_quality {settings.CRF} -pix_fmt nv12 ";
        }
        // HEVC encoders
        else if (encoder == "libx265")
        {
            // HEVC typically achieves same quality at ~30% lower bitrate, so use CRF+2
            var adjustedCrf = Math.Min(settings.CRF + 2, 51);
            return $"-c:v libx265 -preset {settings.Preset} -crf {adjustedCrf} -pix_fmt yuv420p ";
        }
        else if (encoder == "hevc_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            return $"-c:v hevc_nvenc -preset {nvencPreset} -cq {settings.CRF} ";
        }
        else if (encoder == "hevc_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            return $"-c:v hevc_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} ";
        }
        else if (encoder == "hevc_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v hevc_qsv -preset {qsvPreset} -global_quality {settings.CRF} ";
        }
        // AV1 encoders (hardware only)
        else if (encoder == "av1_nvenc")
        {
            var nvencPreset = MapToNvencPreset(settings.Preset);
            // AV1 achieves same quality at even lower bitrate, use CRF+4
            var adjustedCrf = Math.Min(settings.CRF + 4, 63);
            return $"-c:v av1_nvenc -preset {nvencPreset} -cq {adjustedCrf} ";
        }
        else if (encoder == "av1_amf")
        {
            var amfQuality = MapToAmfQuality(settings.Preset);
            return $"-c:v av1_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} ";
        }
        else if (encoder == "av1_qsv")
        {
            var qsvPreset = MapToQsvPreset(settings.Preset);
            return $"-c:v av1_qsv -preset {qsvPreset} -global_quality {settings.CRF} ";
        }
        
        // Fallback
        return $"-c:v libx264 -preset {settings.Preset} -crf {settings.CRF} -pix_fmt yuv420p ";
    }
    
    /// <summary>Map x264 preset to NVENC p1-p7 presets.</summary>
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
    
    /// <summary>Map x264 preset to AMD AMF quality settings.</summary>
    private static string MapToAmfQuality(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" => "speed",
        "faster" or "fast" or "medium" => "balanced",
        _ => "quality"
    };
    
    /// <summary>Map x264 preset to Intel QSV preset.</summary>
    private static string MapToQsvPreset(string preset) => preset switch
    {
        "ultrafast" or "superfast" => "veryfast",
        "veryslow" => "veryslow",
        _ => preset
    };

    /// <summary>
    /// Get video filter for resolution scaling.
    /// </summary>
    private string GetScaleFilter(string maxResolution, bool hasSubtitleOverlay, string hwAccel)
    {
        // For NVIDIA hardware acceleration without subtitle overlay (which requires software processing),
        // use scale_cuda filter. This is CRITICAL for 10-bit inputs (HDR/HEVC 10-bit),
        // For NVIDIA hardware acceleration without subtitle overlay (which requires software processing),
        // use scale_cuda filter. This is CRITICAL for 10-bit inputs (HDR/HEVC 10-bit),
        // as h264_nvenc expects 8-bit input (yuv420p/nv12) or explicit format conversion.
        // Failing to do this causes FFmpeg to crash or fail when trying to feed 10-bit decoded frames
        // directly to the 8-bit encoder.
        if (hwAccel.ToLower() == "nvidia" && !hasSubtitleOverlay)
        {
            var scaleCuda = maxResolution.ToLower() switch
            {
                "720p" => "scale_cuda=1280:-2:format=nv12",
                "1080p" => "scale_cuda=1920:-2:format=nv12",
                "4k" => "scale_cuda=3840:-2:format=nv12",
                _ => "scale_cuda=format=nv12" // Force format conversion even if resolution is original
            };
            
            return $"-vf \"{scaleCuda}\" ";
        }

        // Standard software scaling for other cases
        var scale = maxResolution.ToLower() switch
        {
            "720p" => "scale=1280:-2",
            "1080p" => "scale=1920:-2",
            "4k" => "scale=3840:-2",
            _ => "" // original - no scaling
        };
        
        if (string.IsNullOrEmpty(scale)) return "";
        
        // If we have subtitle overlay, the scale needs to be part of filter_complex
        // Otherwise use -vf
        if (hasSubtitleOverlay)
        {
            return $",{scale}";
        }
        return $"-vf \"{scale}\" ";
    }

    public async Task<MediaProbeResult?> ProbeMediaAsync(string path)
    {
        try
        {
            var ffprobePath = ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams -show_chapters \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            var result = new MediaProbeResult();

            if (doc.RootElement.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var duration))
                {
                    if (double.TryParse(duration.GetString(), out var d))
                        result.Duration = d;
                }
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType)) continue;
                    var type = codecType.GetString();

                    if (type == "video" && result.VideoCodec == null)
                    {
                        if (stream.TryGetProperty("codec_name", out var codec))
                            result.VideoCodec = codec.GetString();
                        if (stream.TryGetProperty("width", out var w) && stream.TryGetProperty("height", out var h))
                            result.Resolution = $"{w.GetInt32()}x{h.GetInt32()}";
                        if (stream.TryGetProperty("pix_fmt", out var pixFmt))
                            result.PixelFormat = pixFmt.GetString();
                        if (stream.TryGetProperty("color_transfer", out var transfer))
                            result.ColorTransfer = transfer.GetString();
                        
                        // Parse frame rate (e.g., "24000/1001" or "24/1")
                        if (stream.TryGetProperty("avg_frame_rate", out var avgFr))
                        {
                            var frStr = avgFr.GetString();
                            if (!string.IsNullOrEmpty(frStr))
                            {
                                var parts = frStr.Split('/');
                                if (parts.Length == 2 && 
                                    double.TryParse(parts[0], out var num) && 
                                    double.TryParse(parts[1], out var den) && 
                                    den > 0)
                                {
                                    result.FrameRate = num / den;
                                }
                                else if (double.TryParse(frStr, out var fps))
                                {
                                    result.FrameRate = fps;
                                }
                            }
                        }
                    }
                    else if (type == "audio" && result.AudioCodec == null)
                    {
                        if (stream.TryGetProperty("codec_name", out var codec))
                            result.AudioCodec = codec.GetString();
                    }
                }
            }

            // Parse chapters to find credits start time
            if (doc.RootElement.TryGetProperty("chapters", out var chapters))
            {
                result.Chapters = new List<(double StartTime, string Title)>();
                
                foreach (var chapter in chapters.EnumerateArray())
                {
                    double startTime = 0;
                    string title = "";
                    
                    // Get start time (in seconds)
                    if (chapter.TryGetProperty("start_time", out var startTimeEl))
                    {
                        double.TryParse(startTimeEl.GetString(), out startTime);
                    }
                    
                    // Get title from tags
                    if (chapter.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("title", out var titleEl))
                        {
                            title = titleEl.GetString() ?? "";
                        }
                    }
                    
                    result.Chapters.Add((startTime, title));
                    
                    // Check if this is a credits chapter
                    var lowerTitle = title.ToLowerInvariant();
                    if (result.CreditsStart == null && 
                        (lowerTitle.Contains("credit") || 
                         lowerTitle.Contains("end credits") ||
                         lowerTitle.Contains("outro") ||
                         lowerTitle.Contains("ending")))
                    {
                        result.CreditsStart = startTime;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to probe media: {Path}", path);
            return null;
        }
    }

    public ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix)
    {
        var settings = LoadSettingsAsync().GetAwaiter().GetResult();
        return GetTranscodeArgumentsInternal(inputPath, outputDir, segmentPrefix, null, null, null, settings);
    }

    public ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition)
    {
        var settings = LoadSettingsAsync().GetAwaiter().GetResult();
        return GetTranscodeArgumentsInternal(inputPath, outputDir, segmentPrefix, subtitleTrackIndex, seekPosition, null, settings);
    }

    public ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate, string? targetResolution = null, string? targetCodec = null, bool preserveHdr = false, int? audioTrackIndex = null)
    {
        var settings = LoadSettingsAsync().GetAwaiter().GetResult();
        // Override settings with URL parameters if explicitly specified
        if (!string.IsNullOrEmpty(targetResolution))
        {
            settings.MaxResolution = targetResolution;
        }
        // Override codec from URL (server already validated/selected optimal codec)
        if (!string.IsNullOrEmpty(targetCodec))
        {
            settings.OutputVideoCodec = targetCodec;
            _logger.LogDebug("Using URL-specified codec: {Codec}", targetCodec);
        }
        // Override HDR setting from URL
        settings.PreserveHDR = preserveHdr;
        
        return GetTranscodeArgumentsInternal(inputPath, outputDir, segmentPrefix, subtitleTrackIndex, seekPosition, readRate, settings, audioTrackIndex);
    }

    /// <summary>
    /// Determines if a subtitle codec is bitmap-based (requires overlay filter)
    /// or text-based (requires subtitles filter).
    /// </summary>
    public static bool IsBitmapSubtitleCodec(string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return false;
        
        // Bitmap subtitle formats that can be overlaid directly
        var bitmapCodecs = new[] 
        { 
            "hdmv_pgs_subtitle", "pgs", 
            "dvd_subtitle", "dvdsub", 
            "xsub",
            "dvb_subtitle"
        };
        
        return bitmapCodecs.Contains(codec.ToLowerInvariant());
    }

    /// <summary>
    /// Probe a file to get the subtitle codec for a specific track.
    /// </summary>
    public async Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex)
    {
        try
        {
            var ffprobePath = ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -select_streams {subtitleTrackIndex} \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_name", out var codecProp))
                    {
                        return codecProp.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe subtitle codec for track {Index} in {Path}", subtitleTrackIndex, inputPath);
        }
        
        return null;
    }

    public async Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex)
    {
        try
        {
            var ffprobePath = ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -select_streams {subtitleTrackIndex} \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("language", out var lang))
                    {
                        return lang.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe subtitle language for track {Index} in {Path}", subtitleTrackIndex, inputPath);
        }
        
        return null;
    }

    /// <summary>
    /// Helper to detect if a file is 10-bit/HDR based on pixel format.
    /// </summary>
    private bool Is10BitOrHdr(string? pixelFormat, string? colorTransfer)
    {
        // Check for known HDR transfer characteristics
        if (!string.IsNullOrEmpty(colorTransfer))
        {
            var tf = colorTransfer.ToLowerInvariant();
            // smpte2084 = PQ (HDR10), arib-std-b67 = HLG
            if (tf == "smpte2084" || tf == "arib-std-b67") return true;
        }

        // Fallback: Check pixel format (less reliable, assumes all 10-bit is HDR)
        // We now PRIORITIZE explicit HDR check above.
        // If it's just 10-bit but transfer is bt709, it's NOT HDR.
        // So we only return true here if we didn't confirm SDR via transfer function.
        
        if (string.IsNullOrEmpty(pixelFormat)) return false;
        var fmt = pixelFormat.ToLowerInvariant();
        bool is10bit = fmt.Contains("10") || fmt.Contains("12") || fmt.Contains("p010") || fmt.Contains("p016");
        
        // If we have explicit SDR transfer, return false even if 10-bit
        if (is10bit && !string.IsNullOrEmpty(colorTransfer) && colorTransfer.ToLowerInvariant() == "bt709")
            return false;
            
        return is10bit;
    }

    private ProcessStartInfo GetTranscodeArgumentsInternal(
        string inputPath, 
        string outputDir, 
        string segmentPrefix, 
        int? subtitleTrackIndex, 
        double? seekPosition,
        double? readRate,
        TranscodeSettings settings,
        int? audioTrackIndex = null)
    {
        Directory.CreateDirectory(outputDir);

        var playlistPath = Path.Combine(outputDir, "master.m3u8");
        // Note: segmentPath is defined later based on container type (fmp4 vs ts)

        // Probe media to check for HDR/10-bit
        var probe = ProbeMediaAsync(inputPath).GetAwaiter().GetResult();
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
            subtitleCodec = ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex!.Value)
                .GetAwaiter().GetResult();
            _logger.LogInformation("Subtitle track {Index} codec: {Codec}", subtitleTrackIndex, subtitleCodec ?? "unknown");
            useTextSubtitles = !IsBitmapSubtitleCodec(subtitleCodec);
            
            // WORKAROUND: FFmpeg's subtitles filter on Windows cannot handle apostrophes in paths
            // regardless of escaping method. Skip text subtitle burn-in for these files.
            if (useTextSubtitles && inputPath.Contains("'"))
            {
                _logger.LogWarning("Skipping text subtitle burn-in for file with apostrophe in path: {Path}", inputPath);
                hasSubtitleOverlay = false;
                useTextSubtitles = false;
            }
            
            // WORKAROUND: When resuming at large positions (>60s), fast seek is required for 
            // reasonable startup time, but this causes text subtitles to be completely desynced
            // (subtitles start from beginning while video is at seek position).
            // Skip text subtitle burn-in in this case - user can restart from beginning if needed.
            const double MaxSeekForTextSubtitles = 60.0;
            if (useTextSubtitles && seekPosition.HasValue && seekPosition.Value > MaxSeekForTextSubtitles)
            {
                _logger.LogWarning("Skipping text subtitle burn-in for large seek position {Seek}s (would be desynced)", seekPosition.Value);
                hasSubtitleOverlay = false;
                useTextSubtitles = false;
            }
        }

        var argumentBuilder = new System.Text.StringBuilder();
        
        // Thread count (if specified)
        if (settings.ThreadCount > 0)
        {
            argumentBuilder.Append($"-threads {settings.ThreadCount} ");
        }

        // SEEK STRATEGY:
        // - For text subtitles: Use SLOW SEEK (-ss after -i) to keep subtitle timing in sync
        //   The subtitles filter reads from the file directly, so fast seek would cause desync
        // - For bitmap subtitles or no subtitles: Use FAST SEEK (-ss before -i) for quick startup
        // - EXCEPTION: For large seek positions (>60s), always use fast seek to avoid
        //   catastrophically slow startup for 4K/HEVC files. Accept minor subtitle desync.
        const double MaxSlowSeekSeconds = 60.0;
        bool seekIsTooLarge = seekPosition.HasValue && seekPosition.Value > MaxSlowSeekSeconds;
        bool useFastSeek = !useTextSubtitles || seekIsTooLarge;
        
        if (seekIsTooLarge && useTextSubtitles)
        {
            _logger.LogInformation("Using fast seek for large position {Seek}s (slow seek would be too slow)", seekPosition.Value);
        }
        
        if (useFastSeek && seekPosition.HasValue && seekPosition.Value > 0)
        {
            // Fast seek: -ss before -i (video starts quickly but subtitles filter would desync)
            argumentBuilder.Append($"-ss {seekPosition.Value:F2} ");
        }

        // Add read rate for throttling (before input)
        if (readRate.HasValue && readRate.Value > 0)
        {
            argumentBuilder.Append($"-readrate {readRate.Value:F1} ");
        }
        
        // HARDWARE DECODE: Add GPU decode options BEFORE the input file
        // This enables full hardware transcoding: GPU decode -> GPU encode (zero-copy)
        var hwDecodeOptions = GetHardwareDecodeOptions(settings.HardwareAcceleration, hasSubtitleOverlay);
        if (!string.IsNullOrEmpty(hwDecodeOptions))
        {
            argumentBuilder.Append(hwDecodeOptions);
            _logger.LogInformation("Using hardware decode: {HwDecode}", hwDecodeOptions.Trim());
        }
        
        // Input file
        argumentBuilder.Append($"-i \"{inputPath}\" ");
        
        // Slow seek: -ss after -i (decodes from start but keeps subtitle timing correct)
        if (!useFastSeek && seekPosition.HasValue && seekPosition.Value > 0)
        {
            argumentBuilder.Append($"-ss {seekPosition.Value:F2} ");
            _logger.LogInformation("Using slow seek for text subtitle synchronization at {Seek}s", seekPosition.Value);
        }
        

        // --- 10-BIT / HDR HANDLING ---
        // For Nvidia + 10-bit/HDR content, we use the Jellyfin-FFmpeg Zero-Copy Pipeline.
        // This requires 'tonemap_cuda' (included in Jellyfin-FFmpeg builds).
        // It provides extremely high performance (>200 FPS) by keeping frames in GPU memory.
        
        // If PreserveHDR is enabled, skip tonemapping and keep HDR output
        bool skipTonemapping = settings.PreserveHDR && is10Bit;
        bool useToneMappingPipeline = is10Bit && settings.HardwareAcceleration.ToLower() == "nvidia" && !skipTonemapping;
        
        if (skipTonemapping)
        {
            _logger.LogInformation("PreserveHDR enabled: skipping tonemapping for 10-bit/HDR content");
        }
        
        string scaleFilter = "";
        
        if (useToneMappingPipeline)
        {
            // ZERO-COPY PIPELINE (Jellyfin-FFmpeg)
            // 1. scale_cuda: Resize content (keeping it in P010/10-bit)
            // 2. tonemap_cuda: Hardware HDR->SDR tone mapping directly in CUDA memory.
            // 3. fps: Normalize frame rate
            
             var scale = settings.MaxResolution.ToLower() switch
            {
                "720p" => "scale_cuda=1280:-2:format=p010", 
                "1080p" => "scale_cuda=1920:-2:format=p010",
                "4k" => "scale_cuda=3840:-2:format=p010",
                _ => "scale_cuda=format=p010" 
            };
            
            // Build filter chain
            var chain = new List<string>();
            chain.Add(scale);
            
            // Tonemap CUDA with configurable algorithm
            // Valid algorithms: hable, reinhard, mobius
            var toneAlgo = settings.ToneMappingAlgorithm.ToLower();
            if (toneAlgo != "hable" && toneAlgo != "reinhard" && toneAlgo != "mobius")
            {
                toneAlgo = "hable"; // Default fallback
            }
            chain.Add($"tonemap_cuda=tonemap={toneAlgo}:format=nv12");
            _logger.LogDebug("Using tonemap algorithm: {Algorithm}", toneAlgo);
            
            // fps filter normalizes frame timing.
            double fps = probe?.FrameRate > 0 ? probe.FrameRate : 24.0;
            chain.Add($"fps={fps}");
            
            string toneMapFilter = string.Join(",", chain);
            
            if (hasSubtitleOverlay)
            {
               _logger.LogWarning("10-bit content with subtitles: bypassing tone mapping to ensure subtitle rendering.");
               scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay, settings.HardwareAcceleration);
               useToneMappingPipeline = false;
            }
            else
            {
                argumentBuilder.Append($"-vf \"{toneMapFilter}\" ");
            }
        }
        
        if (!useToneMappingPipeline)
        {
             // Standard logical pipeline
             scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay, settings.HardwareAcceleration);
        }
        
        if (hasSubtitleOverlay)
        {
            var filterChain = new System.Text.StringBuilder();
            
            if (IsBitmapSubtitleCodec(subtitleCodec))
            {
                // Bitmap subtitles (PGS, DVD): use overlay filter
                // The [v] label captures the output for mapping
                filterChain.Append($"[0:v][0:{subtitleTrackIndex.Value}]overlay");
                
                // Add scaling if needed
                if (!string.IsNullOrEmpty(scaleFilter))
                {
                    filterChain.Append(scaleFilter);
                }
                
                // Add output label for mapping
                filterChain.Append("[v]");
                
                argumentBuilder.Append($"-filter_complex \"{filterChain}\" ");
                // Map the filtered video output and first audio stream
                argumentBuilder.Append("-map \"[v]\" -map 0:a:0 ");
            }
            else
            {
                // Text subtitles (SRT, ASS, SSA, WebVTT): use subtitles filter
                // Escape special characters in Windows paths for FFmpeg filter.
                // Multi-level escaping is needed:
                // Level 1: C# string literal (handled by @"" or regular escaping)
                // Level 2: Process command line parsing
                // Level 3: FFmpeg filter parsing
                // 
                // For single quotes: use \\' (two backslashes in output)
                // The C# string @"\\'" produces the string \\' which FFmpeg sees as \'
                var escapedPath = inputPath
                    .Replace("\\", "/")
                    .Replace(":", "\\:")
                    .Replace("'", @"\\'");
                
                // Build filter: subtitles filter with stream index, then optional scale
                filterChain.Append($"subtitles='{escapedPath}':si={GetSubtitleStreamIndex(inputPath, subtitleTrackIndex!.Value)}");
                
                // Add scaling if needed (chain after subtitles)
                if (!string.IsNullOrEmpty(scaleFilter))
                {
                    filterChain.Append(scaleFilter);
                }
                
                argumentBuilder.Append($"-vf \"{filterChain}\" ");
            }
            
            argumentBuilder.Append(GetEncoderOptions(settings));
        }
        else if (!string.IsNullOrEmpty(scaleFilter))
        {
            // Just scaling, no subtitles
            argumentBuilder.Append(scaleFilter);
            argumentBuilder.Append(GetEncoderOptions(settings));
        }
        else if (useToneMappingPipeline)
        {
            // Tone mapping pipeline already added -vf, just append encoder
            argumentBuilder.Append(GetEncoderOptions(settings));
        }
        else
        {
            // No filters needed
            argumentBuilder.Append(GetEncoderOptions(settings));
        }
        
        // Audio stream mapping (select specific audio track if specified)
        // Note: audioTrackIndex is the absolute FFprobe stream index, not audio-relative
        // IMPORTANT: When using -map, FFmpeg ONLY includes explicitly mapped streams.
        // So if we map audio, we MUST also map video, otherwise video is excluded!
        if (audioTrackIndex.HasValue)
        {
            argumentBuilder.Append("-map 0:v:0 ");  // Map first video stream
            argumentBuilder.Append($"-map 0:{audioTrackIndex.Value} ");  // Map selected audio stream
            _logger.LogInformation("Mapping video stream 0:v:0 and audio stream 0:{Index}", audioTrackIndex.Value);
        }
        
        // Audio encoding
        argumentBuilder.Append("-c:a aac -ac 2 -b:a 128k ");
        
        // Use start_at_zero to reset output timestamps to 0 after seeking.
        // This ensures HLS segments have monotonic timestamps starting from 0,
        // which fixes elapsed time desync, frame hiccups, and random jumps.
        argumentBuilder.Append("-start_at_zero ");
        
        // HLS output settings
        // Note: Using 'event' playlist type + 'append_list' allows live-style growing playlist
        // Do NOT use 'omit_endlist' - FFmpeg needs to write #EXT-X-ENDLIST when done
        
        // Use fMP4 segments when:
        // 1. Preserving HDR (TS doesn't support HDR metadata properly)
        // 2. Using AV1 codec (MPEG-TS doesn't support AV1, browsers require fMP4 for AV1)
        // For regular transcoding with H.264/HEVC, use MPEG-TS which works with append_list
        var codecLower = settings.OutputVideoCodec.ToLower();
        bool useAv1 = codecLower == "av1" || codecLower.Contains("av1");
        bool useFmp4 = skipTonemapping || useAv1;  // HDR passthrough or AV1 requires fMP4
        
        // Determine segment extension based on container type
        var segmentExt = useFmp4 ? "m4s" : "ts";
        var segmentPath = Path.Combine(outputDir, $"{segmentPrefix}_%03d.{segmentExt}");
        
        // HLS output configuration
        // - hls_time 6: Target segment duration
        // - hls_list_size 0: Keep all segments in playlist (VOD-like for seeking)
        // - hls_playlist_type event: Growing playlist (live transcoding)
        argumentBuilder.Append("-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event ");
        
        if (useFmp4)
        {
            // fMP4 mode: Required for HDR metadata preservation
            // - hls_segment_type fmp4: Use fragmented MP4 segments
            // - hls_fmp4_init_filename: Create init segment for codec configuration
            // - hls_flags independent_segments: Each segment starts with keyframe (required for fMP4)
            // NOTE: Do NOT use append_list with fMP4 - it prevents init.mp4 creation
            // NOTE: init.mp4 is relative - FFmpeg WorkingDirectory must be set to outputDir!
            argumentBuilder.Append("-hls_segment_type fmp4 ");
            argumentBuilder.Append("-hls_fmp4_init_filename init.mp4 ");
            argumentBuilder.Append("-hls_flags independent_segments ");
            _logger.LogInformation("Using fMP4 segments (reason: {Reason}, codec={Codec})", 
                useAv1 ? "AV1 requires fMP4" : "HDR passthrough", codecLower);
        }
        else
        {
            // MPEG-TS mode: Standard container, wide compatibility
            // - append_list: Append new segments to playlist (works for live transcoding)
            argumentBuilder.Append("-hls_flags append_list ");
        }
        
        argumentBuilder.Append($"-start_number 0 -hls_segment_filename \"{segmentPath}\" ");
        argumentBuilder.Append($"\"{playlistPath}\"");

        var arguments = argumentBuilder.ToString();
        
        var ffmpegPath = ResolveFFmpegPath();
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
            WorkingDirectory = outputDir  // Ensure init.mp4 is created in the output directory
        };
    }

    /// <summary>
    /// Get the subtitle stream index (relative to subtitle streams only) for the subtitles filter.
    /// The subtitles filter uses 'si' (stream index) which is relative to subtitle streams, not absolute.
    /// </summary>
    public int GetSubtitleStreamIndex(string inputPath, int absoluteStreamIndex)
    {
        try
        {
            var ffprobePath = ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = _processRunner.RunProcessAsync(startInfo).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(output)) return 0;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                int subtitleIndex = 0;
                foreach (var stream in streams.EnumerateArray())
                {
                    var index = stream.GetProperty("index").GetInt32();
                    var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    
                    if (codecType == "subtitle")
                    {
                        if (index == absoluteStreamIndex)
                        {
                            return subtitleIndex;
                        }
                        subtitleIndex++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate subtitle stream index, using 0");
        }
        
        return 0;
    }

    /// <summary>
    /// Resolves the path to ffmpeg executable by checking common installation locations.
    /// </summary>
    private string ResolveFFmpegPath()
    {
        var candidates = new[]
        {
            // Auto-downloaded BtbN build
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", "ffmpeg.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe"),
            @"C:\Program Files\ffmpeg-2024-06-27-git-9a3bc59a38-full_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffmpeg";
    }

    /// <summary>
    /// Resolves the path to ffprobe executable by checking common installation locations.
    /// </summary>
    private string ResolveFFprobePath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", "ffprobe.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "ffprobe.exe"),
            @"C:\Program Files\ffmpeg-2024-06-27-git-9a3bc59a38-full_build\bin\ffprobe.exe",
            @"C:\ffmpeg\bin\ffprobe.exe",
            @"C:\Program Files\ffmpeg\bin\ffprobe.exe",
            @"C:\ProgramData\chocolatey\bin\ffprobe.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffprobe";
    }

    /// <summary>
    /// Extract a subtitle track to WebVTT format for HLS sidecar delivery.
    /// Uses FFmpeg to extract and convert the subtitle stream.
    /// </summary>
    public async Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath)
    {
        try
        {
            var ffmpegPath = ResolveFFmpegPath();
            
            // FFmpeg command to extract subtitle track and convert to WebVTT
            // -i input: input file
            // -map 0:s:{index}: select specific subtitle stream
            // -c:s webvtt: convert to WebVTT format
            // -y: overwrite output file
            var arguments = $"-i \"{inputPath}\" -map 0:s:{subtitleStreamIndex} -c:s webvtt -y \"{outputPath}\"";
            
            _logger.LogInformation("Extracting subtitle track {Index} to WebVTT: {Path}", subtitleStreamIndex, outputPath);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            
            var errorOutput = new System.Text.StringBuilder();
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorOutput.AppendLine(e.Data);
                }
            };
            
            process.Start();
            process.BeginErrorReadLine();
            
            // Wait up to 30 seconds for extraction
            var completed = await Task.Run(() => process.WaitForExit(30000));
            
            if (!completed)
            {
                _logger.LogWarning("Subtitle extraction timed out for {Path}", inputPath);
                try { process.Kill(); } catch { }
                return false;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Subtitle extraction failed (exit code {Code}): {Error}", 
                    process.ExitCode, errorOutput.ToString().Substring(0, Math.Min(500, errorOutput.Length)));
                return false;
            }

            // Verify output file was created
            if (!File.Exists(outputPath))
            {
                _logger.LogWarning("Subtitle extraction did not create output file: {Path}", outputPath);
                return false;
            }

            var fileInfo = new FileInfo(outputPath);
            _logger.LogInformation("Subtitle extracted successfully: {Path} ({Size} bytes)", outputPath, fileInfo.Length);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting subtitle track {Index} from {Path}", subtitleStreamIndex, inputPath);
            return false;
        }
    }

    /// <summary>
    /// Offset all timestamps in a WebVTT file by subtracting the given offset.
    /// This is needed when seeking - the video starts at seek position but plays from time 0.
    /// </summary>
    public void OffsetWebVttTimestamps(string vttPath, double offsetSeconds)
    {
        if (offsetSeconds <= 0 || !File.Exists(vttPath))
            return;

        try
        {
            var lines = File.ReadAllLines(vttPath);
            var offsetTimeSpan = TimeSpan.FromSeconds(offsetSeconds);
            var result = new List<string>();
            var skipCue = false;
            
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                
                // Check if this line contains a timestamp cue (format: HH:MM:SS.mmm --> HH:MM:SS.mmm)
                if (line.Contains(" --> "))
                {
                    var parts = line.Split(new[] { " --> " }, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        if (TryParseVttTimestamp(parts[0].Trim(), out var startTime) && 
                            TryParseVttTimestamp(parts[1].Trim(), out var endTime))
                        {
                            // Subtract offset
                            var newStart = startTime - offsetTimeSpan;
                            var newEnd = endTime - offsetTimeSpan;
                            
                            // Skip cues that end before the offset point
                            if (newEnd < TimeSpan.Zero)
                            {
                                skipCue = true;
                                continue;
                            }
                            
                            // Clamp start to 0 if it goes negative
                            if (newStart < TimeSpan.Zero)
                                newStart = TimeSpan.Zero;
                            
                            result.Add($"{FormatVttTimestamp(newStart)} --> {FormatVttTimestamp(newEnd)}");
                            skipCue = false;
                            continue;
                        }
                    }
                }
                
                // Skip content lines for cues we're skipping
                if (skipCue)
                {
                    // Keep blank lines as they separate cues
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        skipCue = false;
                        result.Add(line);
                    }
                    continue;
                }
                
                result.Add(line);
            }
            
            File.WriteAllLines(vttPath, result);
            _logger.LogInformation("Offset WebVTT timestamps by {Offset}s: {Path}", offsetSeconds, vttPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error offsetting WebVTT timestamps in {Path}", vttPath);
        }
    }
    
    private bool TryParseVttTimestamp(string timestamp, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        // VTT timestamp format: HH:MM:SS.mmm or MM:SS.mmm
        try
        {
            var parts = timestamp.Split(':');
            if (parts.Length == 3)
            {
                // HH:MM:SS.mmm
                var hours = int.Parse(parts[0]);
                var minutes = int.Parse(parts[1]);
                var secondsParts = parts[2].Split('.');
                var seconds = int.Parse(secondsParts[0]);
                var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1].PadRight(3, '0').Substring(0, 3)) : 0;
                result = new TimeSpan(0, hours, minutes, seconds, milliseconds);
                return true;
            }
            else if (parts.Length == 2)
            {
                // MM:SS.mmm
                var minutes = int.Parse(parts[0]);
                var secondsParts = parts[1].Split('.');
                var seconds = int.Parse(secondsParts[0]);
                var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1].PadRight(3, '0').Substring(0, 3)) : 0;
                result = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                return true;
            }
        }
        catch { }
        return false;
    }
    
    private string FormatVttTimestamp(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
