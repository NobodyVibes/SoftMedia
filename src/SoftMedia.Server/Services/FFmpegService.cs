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
            return $"-c:v h264_nvenc -preset {nvencPreset} -cq {settings.CRF} -pix_fmt yuv420p ";
        }
        else if (encoder == "h264_amf")
        {
            // AMD AMF - use quality preset and qp_i/qp_p
            var amfQuality = settings.Preset switch
            {
                "ultrafast" or "superfast" or "veryfast" => "speed",
                "faster" or "fast" or "medium" => "balanced",
                _ => "quality"
            };
            return $"-c:v h264_amf -quality {amfQuality} -rc cqp -qp_i {settings.CRF} -qp_p {settings.CRF} -pix_fmt yuv420p ";
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
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
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

        var argumentBuilder = new System.Text.StringBuilder();
        
        // Thread count (if specified)
        if (settings.ThreadCount > 0)
        {
            argumentBuilder.Append($"-threads {settings.ThreadCount} ");
        }

        // Add seek position if specified (before input for fast seeking)
        if (seekPosition.HasValue && seekPosition.Value > 0)
        {
            argumentBuilder.Append($"-ss {seekPosition.Value:F2} ");
        }

        // Add read rate for throttling (before input)
        if (readRate.HasValue && readRate.Value > 0)
        {
            argumentBuilder.Append($"-readrate {readRate.Value:F1} ");
        }
        
        // Input file
        argumentBuilder.Append($"-i \"{inputPath}\" ");
        
        // Video encoding with optional subtitle burn-in and scaling
        bool hasSubtitleOverlay = subtitleTrackIndex.HasValue;
        string scaleFilter = GetScaleFilter(settings.MaxResolution, hasSubtitleOverlay);
        
        if (hasSubtitleOverlay)
        {
            // Use filter_complex for subtitle overlay (and optional scaling)
            argumentBuilder.Append($"-filter_complex \"[0:v][0:{subtitleTrackIndex.Value}]overlay{scaleFilter}\" ");
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
}
