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
            var ffprobePath = File.Exists("ffprobe.exe") ? "ffprobe.exe" : "ffprobe";
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

        // Basic HLS Transcoding:
        // -c:v libx264: Transcode video to H.264
        // -preset veryfast: Balance speed/quality
        // -c:a aac: Transcode audio to AAC
        // -f hls: Output format HLS
        // -hls_time 6: 6 second segments
        // -hls_list_size 0: Keep all segments in playlist (VOD)
        
        var arguments = $"-i \"{inputPath}\" " +
                        $"-c:v libx264 -preset veryfast -crf 23 " +
                        $"-c:a aac -b:a 128k " +
                        $"-f hls -hls_time 6 -hls_playlist_type event -hls_segment_filename \"{segmentPath}\" " +
                        $"\"{playlistPath}\"";

        var ffmpegPath = File.Exists("ffmpeg.exe") ? "ffmpeg.exe" : "ffmpeg";
        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true // Capture logs for debugging
        };
    }
}
