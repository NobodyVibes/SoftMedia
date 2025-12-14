using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// DTOs for media track information
/// </summary>
public class MediaTrackInfo
{
    public int Index { get; set; }
    public string Type { get; set; } = string.Empty; // "audio", "subtitle"
    public string? Language { get; set; }
    public string? Title { get; set; }
    public string? Codec { get; set; }
    public bool IsDefault { get; set; }
}

public class MediaTracksResponse
{
    public List<MediaTrackInfo> AudioTracks { get; set; } = new();
    public List<MediaTrackInfo> SubtitleTracks { get; set; } = new();
}

/// <summary>
/// Controller for media track information and subtitle extraction.
/// </summary>
[Authorize]
[ApiController]
[Route("api/media")]
public class MediaTracksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<MediaTracksController> _logger;

    public MediaTracksController(AppDbContext context, ILogger<MediaTracksController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get actual duration of the source video file via FFprobe.
    /// This is useful when the media item doesn't have duration in metadata.
    /// </summary>
    [HttpGet("{id}/duration")]
    public async Task<ActionResult<double>> GetDuration(Guid id)
    {
        var mediaItem = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (mediaItem?.Library == null)
        {
            return NotFound("Media item not found");
        }

        if (!System.IO.File.Exists(mediaItem.Path))
        {
            return NotFound("File not found on disk");
        }

        // Security: LFI Protection
        var canonicalPath = Path.GetFullPath(mediaItem.Path);
        var isAuthorized = mediaItem.Library.Paths.Any(p =>
            canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            _logger.LogWarning("LFI attempt blocked in duration: {Path}", mediaItem.Path);
            return Forbid();
        }

        try
        {
            var duration = await ProbeDurationAsync(mediaItem.Path);
            return Ok(duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error probing duration for {Path}", mediaItem.Path);
            return StatusCode(500, "Failed to probe duration");
        }
    }

    /// <summary>
    /// Get audio and subtitle tracks for a media item.
    /// </summary>
    [HttpGet("{id}/tracks")]
    public async Task<ActionResult<MediaTracksResponse>> GetTracks(Guid id)
    {
        var mediaItem = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (mediaItem?.Library == null)
        {
            return NotFound("Media item not found");
        }

        if (!System.IO.File.Exists(mediaItem.Path))
        {
            return NotFound("File not found on disk");
        }

        // Security: LFI Protection
        var canonicalPath = Path.GetFullPath(mediaItem.Path);
        var isAuthorized = mediaItem.Library.Paths.Any(p =>
            canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            _logger.LogWarning("LFI attempt blocked in tracks: {Path}", mediaItem.Path);
            return Forbid();
        }

        try
        {
            var response = await ExtractTracksAsync(mediaItem.Path);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tracks from {Path}", mediaItem.Path);
            return StatusCode(500, "Failed to extract track information");
        }
    }

    /// <summary>
    /// Extract a subtitle track as WebVTT format.
    /// </summary>
    [HttpGet("{id}/subtitles/{trackIndex}")]
    public async Task<IActionResult> GetSubtitle(Guid id, int trackIndex)
    {
        var mediaItem = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (mediaItem?.Library == null)
        {
            return NotFound("Media item not found");
        }

        if (!System.IO.File.Exists(mediaItem.Path))
        {
            return NotFound("File not found on disk");
        }

        // Security: LFI Protection
        var canonicalPath = Path.GetFullPath(mediaItem.Path);
        var isAuthorized = mediaItem.Library.Paths.Any(p =>
            canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            _logger.LogWarning("LFI attempt blocked in subtitles: {Path}", mediaItem.Path);
            return Forbid();
        }

        try
        {
            var webvtt = await ExtractSubtitleAsWebVTTAsync(mediaItem.Path, trackIndex);
            if (string.IsNullOrEmpty(webvtt))
            {
                return NotFound("Subtitle track not found or could not be extracted");
            }

            return Content(webvtt, "text/vtt", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting subtitle {TrackIndex} from {Path}", trackIndex, mediaItem.Path);
            return StatusCode(500, "Failed to extract subtitle");
        }
    }

    /// <summary>
    /// Extract track information using FFprobe.
    /// </summary>
    private async Task<MediaTracksResponse> ExtractTracksAsync(string path)
    {
        var ffprobePath = ResolveFFprobePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v quiet -print_format json -show_streams \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception("Failed to start FFprobe");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var response = new MediaTracksResponse();

        if (string.IsNullOrEmpty(output)) return response;

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        if (!root.TryGetProperty("streams", out var streams)) return response;

        foreach (var stream in streams.EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();
            var index = stream.GetProperty("index").GetInt32();

            // Get tags for language and title
            string? language = null;
            string? title = null;
            if (stream.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("language", out var langProp))
                    language = langProp.GetString();
                if (tags.TryGetProperty("title", out var titleProp))
                    title = titleProp.GetString();
            }

            var isDefault = false;
            if (stream.TryGetProperty("disposition", out var disposition))
            {
                if (disposition.TryGetProperty("default", out var defaultProp))
                    isDefault = defaultProp.GetInt32() == 1;
            }

            var codec = stream.GetProperty("codec_name").GetString();

            if (codecType == "audio")
            {
                // Get additional audio info
                var channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                var channelLayout = channels switch
                {
                    1 => "Mono",
                    2 => "Stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => $"{channels}ch"
                };

                response.AudioTracks.Add(new MediaTrackInfo
                {
                    Index = index,
                    Type = "audio",
                    Language = language,
                    Title = title ?? channelLayout,
                    Codec = codec,
                    IsDefault = isDefault
                });
            }
            else if (codecType == "subtitle")
            {
                // All subtitle formats are now supported via burn-in transcoding
                // Bitmap formats (PGS, VOBSUB) are burned into the video during transcode
                // Text formats (SRT, ASS) work the same way
                response.SubtitleTracks.Add(new MediaTrackInfo
                {
                    Index = index,
                    Type = "subtitle",
                    Language = language,
                    Title = title,
                    Codec = codec,
                    IsDefault = isDefault
                });
            }
        }

        return response;
    }

    /// <summary>
    /// Extract a subtitle track and convert to WebVTT format using FFmpeg.
    /// </summary>
    private async Task<string?> ExtractSubtitleAsWebVTTAsync(string path, int trackIndex)
    {
        var ffmpegPath = ResolveFFmpegPath();
        
        _logger.LogInformation("Extracting subtitle track {TrackIndex} from {Path} using {FFmpeg}", 
            trackIndex, path, ffmpegPath);
        
        // Use FFmpeg to extract subtitle and convert to WebVTT
        // Note: Some subtitle formats (like PGS/bitmap) cannot be converted to WebVTT
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            // -c:s webvtt explicitly converts subtitles to webvtt format
            Arguments = $"-i \"{path}\" -map 0:{trackIndex} -c:s webvtt -f webvtt pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogDebug("FFmpeg command: {FileName} {Args}", startInfo.FileName, startInfo.Arguments);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception("Failed to start FFmpeg");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();
        
        var output = await outputTask;
        var errorOutput = await errorTask;

        if (!string.IsNullOrEmpty(errorOutput))
        {
            _logger.LogDebug("FFmpeg stderr: {Error}", errorOutput);
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("FFmpeg subtitle extraction failed with exit code {ExitCode} for track {Index} in {Path}. Error: {Error}", 
                process.ExitCode, trackIndex, path, errorOutput);
            return null;
        }
        
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning("FFmpeg returned empty output for subtitle track {Index} in {Path}", trackIndex, path);
            return null;
        }

        _logger.LogInformation("Successfully extracted {Length} bytes of WebVTT subtitle for track {Index}", 
            output.Length, trackIndex);
        return output;
    }

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
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return "ffprobe";
    }

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
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return "ffmpeg";
    }

    /// <summary>
    /// Probe media file duration using FFprobe.
    /// </summary>
    private async Task<double> ProbeDurationAsync(string path)
    {
        var ffprobePath = ResolveFFprobePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v quiet -print_format json -show_format \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception("Failed to start FFprobe");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (string.IsNullOrEmpty(output)) return 0;

        using var doc = JsonDocument.Parse(output);
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var durationProp))
            {
                if (double.TryParse(durationProp.GetString(), out var duration))
                {
                    return duration;
                }
            }
        }

        return 0;
    }
}
