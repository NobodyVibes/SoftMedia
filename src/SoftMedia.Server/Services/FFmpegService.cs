using System.Diagnostics;
using System.Text.Json;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

public interface IFFmpegService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
    ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix);
}

public class MediaProbeResult
{
    public double Duration { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
}

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly IProcessRunner _processRunner;

    public FFmpegService(ILogger<FFmpegService> logger, IProcessRunner processRunner)
    {
        _logger = logger;
        _processRunner = processRunner;
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
            var root = doc.RootElement;
            
            var format = root.GetProperty("format");
            var duration = double.Parse(format.GetProperty("duration").GetString() ?? "0");

            string? videoCodec = null;
            string? audioCodec = null;
            string? resolution = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.GetProperty("codec_type").GetString();
                    if (codecType == "video")
                    {
                        videoCodec = stream.GetProperty("codec_name").GetString();
                        var width = stream.GetProperty("width").GetInt32();
                        var height = stream.GetProperty("height").GetInt32();
                        resolution = $"{width}x{height}";
                    }
                    else if (codecType == "audio" && audioCodec == null)
                    {
                        audioCodec = stream.GetProperty("codec_name").GetString();
                    }
                }
            }

            return new MediaProbeResult
            {
                Duration = duration,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Resolution = resolution
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error probing media: {path}");
            return null;
        }
    }

    public ProcessStartInfo GetTranscodeArguments(string inputPath, string outputDir, string segmentPrefix)
    {
        // Ensure output directory exists
        Directory.CreateDirectory(outputDir);

        var playlistPath = Path.Combine(outputDir, "master.m3u8");
        var segmentPath = Path.Combine(outputDir, $"{segmentPrefix}_%03d.ts");

        // Basic HLS Transcoding with maximum browser compatibility:
        // -c:v libx264: Transcode video to H.264
        // -profile:v baseline -level 3.1: Maximum browser compatibility profile
        // -pix_fmt yuv420p: Standard pixel format for web playback
        // -preset veryfast: Balance speed/quality
        // -c:a aac -ac 2: Transcode audio to stereo AAC
        // -f hls: Output format HLS
        // -hls_time 6: 6 second segments
        // -hls_list_size 0: Keep all segments in playlist (VOD)
        // -start_number 0: Start segment numbering from 0
        
        var arguments = $"-i \"{inputPath}\" " +
                        $"-c:v libx264 -profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                        $"-preset veryfast -crf 23 " +
                        $"-c:a aac -ac 2 -b:a 128k " +
                        $"-f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event " +
                        $"-hls_flags append_list+omit_endlist " +
                        $"-start_number 0 -hls_segment_filename \"{segmentPath}\" " +
                        $"\"{playlistPath}\"";

        // Resolve FFmpeg path - check common installation locations
        var ffmpegPath = ResolveFFmpegPath();
        _logger.LogInformation("Using FFmpeg at: {Path}", ffmpegPath);
        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true // Capture logs for debugging
        };
    }

    /// <summary>
    /// Resolves the path to ffmpeg executable by checking common installation locations.
    /// </summary>
    private string ResolveFFmpegPath()
    {
        // Try common locations in order of preference
        var candidates = new[]
        {
            // Current directory
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe"),
            // Known installation path on this system
            @"C:\Program Files\ffmpeg-2024-06-27-git-9a3bc59a38-full_build\bin\ffmpeg.exe",
            // Generic Program Files locations
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            // Chocolatey installation
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to PATH (will fail if not in PATH, but Process will give clear error)
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
