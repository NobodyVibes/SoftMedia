using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for determining optimal audio streaming strategy.
/// Compares source format against client-declared capabilities.
/// </summary>
public class AudioStreamPlanService : IAudioStreamPlanService
{
    private readonly AppDbContext _context;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AudioStreamPlanService> _logger;

    // Valid audio codecs for transcoding (security allowlist)
    private static readonly HashSet<string> ValidAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp3", "flac", "opus", "vorbis", "ogg", "wav", "alac", "ac3", "eac3", "dts", "pcm"
    };

    // Codec normalization mappings
    private static readonly Dictionary<string, string> CodecAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4a"] = "aac",
        ["m4a"] = "aac",
        ["mpeg"] = "mp3",
        ["mp3float"] = "mp3",
        ["pcm_s16le"] = "wav",
        ["pcm_s24le"] = "wav",
        ["pcm_s32le"] = "wav",
        ["pcm_f32le"] = "wav",
    };

    // MIME types by codec
    private static readonly Dictionary<string, string> CodecToMimeType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = "audio/mpeg",
        ["aac"] = "audio/aac",
        ["flac"] = "audio/flac",
        ["opus"] = "audio/ogg",
        ["vorbis"] = "audio/ogg",
        ["ogg"] = "audio/ogg",
        ["wav"] = "audio/wav",
        ["alac"] = "audio/mp4",
        ["ac3"] = "audio/ac3",
        ["eac3"] = "audio/eac3",
    };

    // Bitrate limits
    private const int MinBitrate = 64;
    private const int MaxBitrate = 320;
    private const int DefaultTranscodeBitrate = 256;

    public AudioStreamPlanService(
        AppDbContext context,
        ISettingsService settingsService,
        ILogger<AudioStreamPlanService> logger)
    {
        _context = context;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<AudioStreamPlan> ComputePlanAsync(
        Guid mediaId,
        string[] clientAudioCodecs,
        int clientMaxBitrate)
    {
        // 1. Fetch media item
        var item = await _context.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaId);

        if (item == null)
        {
            throw new InvalidOperationException($"Media item {mediaId} not found");
        }

        // Validate media type
        if (item.Type != MediaType.Audio)
        {
            throw new InvalidOperationException($"Media item {mediaId} is not an audio track");
        }

        // 2. Normalize source codec
        var sourceCodec = NormalizeCodec(item.AudioCodec ?? "unknown");
        
        // 3. Sanitize client codecs
        var sanitizedClientCodecs = clientAudioCodecs
            .Select(NormalizeCodec)
            .Where(c => ValidAudioCodecs.Contains(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ensure at least AAC fallback
        if (sanitizedClientCodecs.Count == 0)
        {
            sanitizedClientCodecs.Add("aac");
        }

        // 4. Read server bitrate limit
        var serverMaxBitrate = await _settingsService.GetSettingAsync("MaxAudioStreamingBitrate", 0);

        // 5. Determine effective bitrate (min of server and client, ignoring 0 = unlimited)
        int? effectiveBitrate = ComputeEffectiveBitrate(serverMaxBitrate, clientMaxBitrate);

        // 6. Check direct play capability
        var canDirectPlay = sanitizedClientCodecs.Contains(sourceCodec);

        _logger.LogInformation(
            "Audio stream plan for {Id}: Source={Source}, ClientCodecs=[{Codecs}], CanDirectPlay={DP}, Bitrate={BR}",
            mediaId, sourceCodec, string.Join(",", sanitizedClientCodecs), canDirectPlay, effectiveBitrate?.ToString() ?? "unlimited");

        if (canDirectPlay)
        {
            // Direct play - serve original file
            var contentType = GetMimeType(sourceCodec, item.Path);
            return new AudioStreamPlan
            {
                CanDirectPlay = true,
                SourceCodec = sourceCodec,
                TargetCodec = null,
                TargetBitrate = null, // Original quality
                Url = $"/api/v1/audio/stream/{mediaId}",
                ContentType = contentType,
                FilePath = item.Path,
                Duration = item.Duration
            };
        }

        // Transcode required - use AAC as universal fallback
        var targetCodec = "aac";
        var targetBitrate = effectiveBitrate ?? DefaultTranscodeBitrate;

        return new AudioStreamPlan
        {
            CanDirectPlay = false,
            SourceCodec = sourceCodec,
            TargetCodec = targetCodec,
            TargetBitrate = targetBitrate,
            Url = $"/api/v1/audio/transcode/{mediaId}?bitrate={targetBitrate}",
            ContentType = "audio/aac",
            FilePath = item.Path,
            Duration = item.Duration
        };
    }

    /// <summary>
    /// Normalize codec name to canonical form.
    /// </summary>
    private static string NormalizeCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return "unknown";
        
        var lower = codec.ToLowerInvariant().Trim();
        
        // Check aliases
        if (CodecAliases.TryGetValue(lower, out var alias))
            return alias;

        return lower;
    }

    /// <summary>
    /// Compute effective bitrate from server and client limits.
    /// Returns null if both are unlimited (0).
    /// </summary>
    private static int? ComputeEffectiveBitrate(int serverMax, int clientMax)
    {
        // Filter out 0 (unlimited)
        var limits = new[] { serverMax, clientMax }.Where(b => b > 0).ToList();
        
        if (limits.Count == 0)
            return null; // Both unlimited
        
        // Take minimum and clamp to valid range
        var min = limits.Min();
        return Math.Clamp(min, MinBitrate, MaxBitrate);
    }

    /// <summary>
    /// Get MIME type for audio codec.
    /// </summary>
    private static string GetMimeType(string codec, string? filePath)
    {
        if (CodecToMimeType.TryGetValue(codec, out var mime))
            return mime;

        // Fallback to file extension
        if (!string.IsNullOrEmpty(filePath))
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".flac" => "audio/flac",
                ".m4a" or ".aac" => "audio/aac",
                ".ogg" or ".opus" => "audio/ogg",
                ".wav" => "audio/wav",
                _ => "application/octet-stream"
            };
        }

        return "application/octet-stream";
    }
}
