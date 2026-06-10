using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for audio streaming with direct play and transcoding support.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/audio")]
public class AudioStreamController : ControllerBase
{
    private readonly IAudioStreamPlanService _audioStreamPlanService;
    private readonly IMediaService _mediaService;
    private readonly ITranscodeProfileBuilder _transcodeProfileBuilder;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly ILogger<AudioStreamController> _logger;

    public AudioStreamController(
        IAudioStreamPlanService audioStreamPlanService,
        IMediaService mediaService,
        ITranscodeProfileBuilder transcodeProfileBuilder,
        IBinaryLocationService binaryLocationService,
        ILogger<AudioStreamController> logger)
    {
        _audioStreamPlanService = audioStreamPlanService;
        _mediaService = mediaService;
        _transcodeProfileBuilder = transcodeProfileBuilder;
        _binaryLocationService = binaryLocationService;
        _logger = logger;
    }

    /// <summary>
    /// Request DTO for audio stream plan.
    /// </summary>
    public record AudioStreamPlanRequest(
        string[] AudioCodecs,
        int MaxBitrate = 0);

    /// <summary>
    /// Get the optimal stream plan for an audio track.
    /// Client sends its supported codecs and bitrate preference.
    /// </summary>
    [HttpPost("stream-plan/{id}")]
    public async Task<ActionResult<AudioStreamPlan>> GetStreamPlan(
        Guid id,
        [FromBody] AudioStreamPlanRequest request)
    {
        try
        {
            var plan = await _audioStreamPlanService.ComputePlanAsync(
                id,
                request.AudioCodecs ?? ["aac"],
                request.MaxBitrate);

            return Ok(plan);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to compute audio stream plan for {Id}", id);
            return NotFound("Audio stream plan could not be computed for this media item.");
        }
    }

    /// <summary>
    /// Direct play stream - serves original audio file with Range Request support.
    /// </summary>
    [HttpGet("stream/{id}")]
    [HttpHead("stream/{id}")]
    public async Task<IActionResult> GetStream(Guid id)
    {
        try
        {
            var streamInfo = await _mediaService.GetStreamInfoAsync(id);

            if (streamInfo == null)
            {
                return NotFound("Media item not found");
            }

            _logger.LogDebug("Direct play audio stream for {Id}: {Path}", id, streamInfo.Path);

            // Serve file with HTTP Range Request support for seeking
            return PhysicalFile(streamInfo.Path, streamInfo.ContentType, enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Transcoded audio stream - converts to AAC/MP3 with optional bitrate limit.
    /// </summary>
    [HttpGet("transcode/{id}")]
    public async Task<IActionResult> GetTranscodedStream(
        Guid id,
        [FromQuery] int bitrate = 256,
        [FromQuery] string codec = "aac")
    {
        try
        {
            var streamInfo = await _mediaService.GetStreamInfoAsync(id);

            if (streamInfo == null)
            {
                return NotFound("Media item not found");
            }

            // Validate and clamp bitrate
            bitrate = Math.Clamp(bitrate, 64, 320);

            // Validate codec against allowlist
            var allowedCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "opus" };
            if (!allowedCodecs.Contains(codec))
            {
                codec = "aac";
            }

            _logger.LogInformation("Transcoding audio {Id} to {Codec}@{Bitrate}kbps", id, codec, bitrate);

            // Build FFmpeg arguments
            var args = BuildAudioTranscodeArgs(streamInfo.Path, codec, bitrate);

            // Get FFmpeg path
            var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();

            // Determine content type
            var contentType = codec switch
            {
                "aac" => "audio/aac",
                "mp3" => "audio/mpeg",
                "opus" => "audio/ogg",
                _ => "audio/aac"
            };

            // Start FFmpeg process.
            // Security (audit M2): pass each token via ArgumentList rather than joining into
            // an Arguments string. ArgumentList is not re-tokenized, so a media filename can
            // never break out of the input argument to inject ffmpeg options.
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            var process = new Process { StartInfo = startInfo };

            process.Start();

            // Log stderr for debugging (but don't block)
            _ = Task.Run(async () =>
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrEmpty(stderr))
                {
                    _logger.LogDebug("FFmpeg stderr for {Id}: {Stderr}", id, stderr);
                }
            });

            // Return the stdout stream
            return new FileStreamResult(process.StandardOutput.BaseStream, contentType)
            {
                EnableRangeProcessing = false // Transcoded streams don't support seeking
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transcode audio {Id}", id);
            return StatusCode(500, "Transcoding failed");
        }
    }

    /// <summary>
    /// Build FFmpeg arguments for audio transcoding.
    /// </summary>
    private static string[] BuildAudioTranscodeArgs(string inputPath, string codec, int bitrate)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            // No surrounding quotes: ArgumentList passes this verbatim as a single argv
            // element, so the path is never re-parsed (audit M2).
            "-i", inputPath,
            "-vn" // No video
        };

        // Add codec-specific options
        switch (codec.ToLowerInvariant())
        {
            case "aac":
                args.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k", "-f", "adts"]);
                break;
            case "mp3":
                args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{bitrate}k", "-f", "mp3"]);
                break;
            case "opus":
                args.AddRange(["-c:a", "libopus", "-b:a", $"{bitrate}k", "-f", "ogg"]);
                break;
            default:
                args.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k", "-f", "adts"]);
                break;
        }

        // Output to stdout
        args.Add("pipe:1");

        return args.ToArray();
    }
}
