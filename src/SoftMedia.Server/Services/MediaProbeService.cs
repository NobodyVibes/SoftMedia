using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

public interface IMediaProbeService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
    Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex);
    Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex);
}

public class MediaProbeService : IMediaProbeService
{
    private readonly ILogger<MediaProbeService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IBinaryLocationService _binaryLocationService;

    public MediaProbeService(
        ILogger<MediaProbeService> logger,
        IProcessRunner processRunner,
        IBinaryLocationService binaryLocationService)
    {
        _logger = logger;
        _processRunner = processRunner;
        _binaryLocationService = binaryLocationService;
    }

    public async Task<MediaProbeResult?> ProbeMediaAsync(string path)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
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
            _logger.LogError(ex, "Error probing media file: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Probe a file to get the subtitle codec for a specific track.
    /// </summary>
    public async Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
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
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
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
