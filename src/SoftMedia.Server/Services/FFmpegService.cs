using System.Diagnostics;
using System.Text.Json;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

public interface IFFmpegService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
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
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
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
}
