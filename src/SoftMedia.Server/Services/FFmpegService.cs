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
    /// Get transcode arguments with subtitle, seek, and read rate for throttling.
    /// </summary>
    ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate);
    
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
    public List<(double StartTime, string Title)>? Chapters { get; set; }  // All chapters
}

/// <summary>
/// Cached transcoding settings loaded from the database.
/// </summary>
public class TranscodeSettings
{
    public bool DisableTranscoding { get; set; } = false;
    public string HardwareAcceleration { get; set; } = "none";
    public string Preset { get; set; } = "veryfast";
    public int ThreadCount { get; set; } = 0;
    public string MaxResolution { get; set; } = "original";
    public int CRF { get; set; } = 23;
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
        var disableStr = await _settingsService.GetSettingAsync("DisableTranscoding", "false");
        var hwAccel = await _settingsService.GetSettingAsync("HardwareAcceleration", "none");
        var preset = await _settingsService.GetSettingAsync("TranscodePreset", "veryfast");
        var threadCountStr = await _settingsService.GetSettingAsync("TranscodeThreadCount", "0");
        var maxRes = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
        var crfStr = await _settingsService.GetSettingAsync("TranscodeCRF", "23");
        
        return new TranscodeSettings
        {
            DisableTranscoding = bool.TryParse(disableStr, out var disable) && disable,
            HardwareAcceleration = hwAccel,
            Preset = preset,
            ThreadCount = int.TryParse(threadCountStr, out var tc) ? tc : 0,
            MaxResolution = maxRes,
            CRF = int.TryParse(crfStr, out var crf) ? crf : 23
        };
    }

    /// <summary>
    /// Check if transcoding is disabled in settings.
    /// </summary>
    public async Task<bool> IsTranscodingDisabledAsync()
    {
        var value = await _settingsService.GetSettingAsync("DisableTranscoding", "false");
        return bool.TryParse(value, out var disabled) && disabled;
    }

    /// <summary>
    /// Get the video encoder based on hardware acceleration setting.
    /// </summary>
    private string GetVideoEncoder(string hwAccel)
    {
        return hwAccel.ToLower() switch
        {
            "nvidia" => "h264_nvenc",
            "amd" => "h264_amf",
            "intel" => "h264_qsv",
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
    /// Get encoder-specific options based on hardware/software selection.
    /// </summary>
    private string GetEncoderOptions(TranscodeSettings settings)
    {
        var encoder = GetVideoEncoder(settings.HardwareAcceleration);
        
        if (encoder == "libx264")
        {
            // Software encoding - use preset and CRF
            return $"-c:v libx264 -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                   $"-preset {settings.Preset} -crf {settings.CRF} ";
        }
        else if (encoder == "h264_nvenc")
        {
            // NVIDIA NVENC - use p1-p7 presets and CQ (constant quality)
            // Note: When using -hwaccel cuda -hwaccel_output_format cuda, frames stay in GPU memory
            // NVENC accepts CUDA frames directly, so we don't specify -pix_fmt (would cause crash)
            var nvencPreset = settings.Preset switch
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
            return $"-c:v h264_nvenc -preset {nvencPreset} -cq {settings.CRF} ";
        }
        else if (encoder == "h264_amf")
        {
            // AMD AMF - use quality preset and qp_i/qp_p
            // Note: When using -hwaccel d3d11va, frames are in D3D11 texture format
            // We don't specify -pix_fmt to avoid conflicts (FFmpeg ticket #6990)
            var amfQuality = settings.Preset switch
            {
                "ultrafast" or "superfast" or "veryfast" => "speed",
                "faster" or "fast" or "medium" => "balanced",
                _ => "quality"
            };
            return $"-c:v h264_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} ";
        }
        else if (encoder == "h264_qsv")
        {
            // Intel QuickSync - use preset and global_quality
            var qsvPreset = settings.Preset switch
            {
                "ultrafast" or "superfast" => "veryfast",
                "veryslow" => "veryslow",
                _ => settings.Preset
            };
            return $"-c:v h264_qsv -preset {qsvPreset} -global_quality {settings.CRF} -pix_fmt nv12 ";
        }
        
        return $"-c:v libx264 -preset {settings.Preset} -crf {settings.CRF} -pix_fmt yuv420p ";
    }

    /// <summary>
    /// Get video filter for resolution scaling.
    /// </summary>
    private string GetScaleFilter(string maxResolution, bool hasSubtitleOverlay)
    {
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

    public ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate)
    {
        var settings = LoadSettingsAsync().GetAwaiter().GetResult();
        return GetTranscodeArgumentsInternal(inputPath, outputDir, segmentPrefix, subtitleTrackIndex, seekPosition, readRate, settings);
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

    private ProcessStartInfo GetTranscodeArgumentsInternal(
        string inputPath, 
        string outputDir, 
        string segmentPrefix, 
        int? subtitleTrackIndex, 
        double? seekPosition,
        double? readRate,
        TranscodeSettings settings)
    {
        Directory.CreateDirectory(outputDir);

        var playlistPath = Path.Combine(outputDir, "master.m3u8");
        var segmentPath = Path.Combine(outputDir, $"{segmentPrefix}_%03d.ts");

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
        
        // Video encoding with optional subtitle burn-in and scaling
        string scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay);
        
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
        else
        {
            // No filters needed
            argumentBuilder.Append(GetEncoderOptions(settings));
        }
        
        // Audio encoding
        argumentBuilder.Append("-c:a aac -ac 2 -b:a 128k ");
        
        // HLS output settings
        // Note: Using 'event' playlist type + 'append_list' allows live-style growing playlist
        // Do NOT use 'omit_endlist' - FFmpeg needs to write #EXT-X-ENDLIST when done
        argumentBuilder.Append("-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event ");
        argumentBuilder.Append("-hls_flags append_list ");
        argumentBuilder.Append($"-start_number 0 -hls_segment_filename \"{segmentPath}\" ");
        argumentBuilder.Append($"\"{playlistPath}\"");

        var arguments = argumentBuilder.ToString();
        
        var ffmpegPath = ResolveFFmpegPath();
        _logger.LogInformation("FFmpeg command: {Path} {Args}", ffmpegPath, arguments);
        _logger.LogInformation("Transcode settings: HW={HW}, Preset={Preset}, CRF={CRF}, Threads={Threads}, Resolution={Res}", 
            settings.HardwareAcceleration, settings.Preset, settings.CRF, settings.ThreadCount, settings.MaxResolution);

        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
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
