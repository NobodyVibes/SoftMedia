using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

/// <summary>
/// Service for determining the optimal streaming strategy based on client capabilities and source media.
/// </summary>
public interface IStreamPlanService
{
    /// <summary>
    /// Compute the optimal stream plan for a media item given client capabilities.
    /// </summary>
    Task<StreamPlan> ComputeStreamPlanAsync(Guid mediaId, MediaItem mediaItem, ClientCapabilities clientCaps, string token);
}

public class StreamPlanService : IStreamPlanService
{
    private readonly IFFmpegService _ffmpegService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<StreamPlanService> _logger;

    // AllowLists for security - only these codecs are valid (prevents command injection)
    private static readonly HashSet<string> ValidVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "avc", "avc1", "hevc", "h265", "vp8", "vp9", "av1", "mpeg2", "mpeg4", "vc1", "theora"
    };

    private static readonly HashSet<string> ValidAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3", "dts", "truehd", "flac", "mp3", "opus", "vorbis", "pcm", "alac"
    };

    private static readonly HashSet<string> ValidContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mkv", "avi", "mov", "m4v", "ogg", "ts", "hls"
    };

    // Browser-compatible formats for Direct Play (no transcoding)
    private static readonly HashSet<string> DirectPlayContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "ogg", "mov", "m4v"
    };

    private static readonly HashSet<string> DirectPlayVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "avc", "avc1", "vp8", "vp9"  // Note: HEVC/AV1 require explicit client capability check
    };

    private static readonly HashSet<string> DirectPlayAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp3", "opus", "vorbis", "flac"
    };

    // Resource limits for security
    private const int MaxAllowedBitrate = 100_000; // 100 Mbps
    private const int MaxAllowedResolution = 4320; // 8K

    public StreamPlanService(IFFmpegService ffmpegService, ISettingsService settingsService, ILogger<StreamPlanService> logger)
    {
        _ffmpegService = ffmpegService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<StreamPlan> ComputeStreamPlanAsync(Guid mediaId, MediaItem mediaItem, ClientCapabilities clientCaps, string token)
    {
        // ========== Read server streaming settings ==========
        var maxServerBitrate = await _settingsService.GetSettingAsync("MaxStreamingBitrate", 20000);
        var forceDirectPlay = await _settingsService.GetSettingAsync("ForceDirectPlayWhenPossible", true);
        var defaultQuality = await _settingsService.GetSettingAsync("DefaultStreamingQuality", "auto");
        var defaultAudioChannels = await _settingsService.GetSettingAsync("DefaultAudioChannels", "auto");
        
        _logger.LogDebug("Streaming settings: MaxBitrate={Bitrate}kbps, ForceDirectPlay={FDP}, Quality={Q}, Audio={A}",
            maxServerBitrate, forceDirectPlay, defaultQuality, defaultAudioChannels);

        // Sanitize client capabilities
        var sanitizedCaps = SanitizeCapabilities(clientCaps);
        
        // Apply server-side bitrate limit if lower than client's
        if (maxServerBitrate > 0 && (sanitizedCaps.MaxBitrate <= 0 || sanitizedCaps.MaxBitrate > maxServerBitrate))
        {
            _logger.LogDebug("Clamping client bitrate {ClientBitrate} to server max {ServerMax}",
                sanitizedCaps.MaxBitrate, maxServerBitrate);
            sanitizedCaps.MaxBitrate = maxServerBitrate;
        }
        
        // Apply quality setting: client's RequestedQuality takes priority, then server default
        var effectiveQuality = !string.IsNullOrEmpty(clientCaps.RequestedQuality) && clientCaps.RequestedQuality != "auto"
            ? clientCaps.RequestedQuality
            : defaultQuality;
            
        if (effectiveQuality != "auto" && effectiveQuality != "original")
        {
            var qualityResolution = ParseQualityToResolution(effectiveQuality);
            if (qualityResolution > 0)
            {
                _logger.LogDebug("Applying quality {Quality} -> max resolution {Res}p", effectiveQuality, qualityResolution);
                sanitizedCaps.MaxResolution = qualityResolution;
            }
        }

        
        // Apply default audio channels preference
        if (defaultAudioChannels != "auto")
        {
            var channels = ParseAudioChannels(defaultAudioChannels);
            if (channels > 0 && sanitizedCaps.MaxAudioChannels > channels)
            {
                sanitizedCaps.MaxAudioChannels = channels;
            }
        }

        // Probe the source file
        var probe = await _ffmpegService.ProbeMediaAsync(mediaItem.Path);
        if (probe == null)
        {
            _logger.LogWarning("Could not probe media {Id}, defaulting to transcode", mediaId);
            return CreateTranscodePlan(mediaId, mediaItem, sanitizedCaps, token, "Unable to probe source file");
        }

        var sourceVideoCodec = NormalizeCodecName(mediaItem.VideoCodec ?? probe.VideoCodec ?? "");
        var sourceAudioCodec = NormalizeCodecName(mediaItem.AudioCodec ?? probe.AudioCodec ?? "");
        var sourceContainer = NormalizeContainerName(mediaItem.Container ?? "");
        var sourceIsHdr = IsHdrContent(probe.PixelFormat, probe.ColorTransfer);
        var sourceResolution = ParseResolutionHeight(probe.Resolution);

        _logger.LogInformation(
            "Stream plan for {Id}: Container={Container}, Video={Video}, Audio={Audio}, HDR={HDR}, Resolution={Res}",
            mediaId, sourceContainer, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution);

        // Check 1: Can the client Direct Play this file?
        if (forceDirectPlay && CanDirectPlay(sourceContainer, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps))
        {
            return CreateDirectPlayPlan(mediaId, mediaItem, sourceVideoCodec, sourceAudioCodec, sourceContainer, sourceIsHdr, sourceResolution, token);
        }

        // Check 2: Can we Remux (copy streams, change container)?
        // Remux is possible if codecs are supported but container isn't
        if (CanRemux(sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps))
        {
            return CreateRemuxPlan(mediaId, mediaItem, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps, token);
        }

        // Fallback: Full Transcode
        return CreateTranscodePlan(mediaId, mediaItem, sanitizedCaps, token, DetermineTranscodeReason(sourceVideoCodec, sourceAudioCodec, sourceContainer, sourceIsHdr, sourceResolution, sanitizedCaps));
    }
    
    /// <summary>
    /// Parse quality string like "720p", "1080p", "4k" to resolution height.
    /// </summary>
    private static int ParseQualityToResolution(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "720p" => 720,
            "1080p" => 1080,
            "4k" or "2160p" => 2160,
            _ => 0
        };
    }
    
    /// <summary>
    /// Parse audio channel preference to channel count.
    /// </summary>
    private static int ParseAudioChannels(string preference)
    {
        return preference.ToLowerInvariant() switch
        {
            "stereo" => 2,
            "5.1" => 6,
            "7.1" => 8,
            _ => 0
        };
    }


    /// <summary>
    /// Sanitize client capabilities against AllowLists and resource limits.
    /// </summary>
    private ClientCapabilities SanitizeCapabilities(ClientCapabilities caps)
    {
        return new ClientCapabilities
        {
            VideoCodecs = caps.VideoCodecs?.Where(c => ValidVideoCodecs.Contains(NormalizeCodecName(c))).ToArray() ?? ["h264"],
            AudioCodecs = caps.AudioCodecs?.Where(c => ValidAudioCodecs.Contains(NormalizeCodecName(c))).ToArray() ?? ["aac"],
            SupportedContainers = caps.SupportedContainers?.Where(c => ValidContainers.Contains(c.ToLowerInvariant())).ToArray() ?? ["mp4", "webm"],
            SupportedSubtitleFormats = caps.SupportedSubtitleFormats ?? ["vtt"],
            MaxAudioChannels = Math.Clamp(caps.MaxAudioChannels, 2, 8),
            MaxBitrate = caps.MaxBitrate <= 0 ? MaxAllowedBitrate : Math.Clamp(caps.MaxBitrate, 1000, MaxAllowedBitrate),
            MaxResolution = caps.MaxResolution <= 0 ? MaxAllowedResolution : Math.Clamp(caps.MaxResolution, 480, MaxAllowedResolution),
            SupportsHdr = caps.SupportsHdr
        };
    }

    private bool CanDirectPlay(string container, string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps)
    {
        // Container must be directly playable
        if (!DirectPlayContainers.Contains(container) && !caps.SupportedContainers.Contains(container))
            return false;

        // Video codec must be supported by browser OR explicitly listed in client caps
        if (!DirectPlayVideoCodecs.Contains(videoCodec) && !caps.VideoCodecs.Any(c => NormalizeCodecName(c) == videoCodec))
            return false;

        // Audio codec must be supported
        if (!DirectPlayAudioCodecs.Contains(audioCodec) && !caps.AudioCodecs.Any(c => NormalizeCodecName(c) == audioCodec))
            return false;

        // HDR content requires HDR-capable client
        if (isHdr && !caps.SupportsHdr)
            return false;

        // Resolution check
        if (caps.MaxResolution > 0 && resolution > caps.MaxResolution)
            return false;

        return true;
    }

    private bool CanRemux(string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps)
    {
        // For remux, we just copy streams to HLS/MP4 container
        // Video codec must be playable in their browser
        var codecSupported = DirectPlayVideoCodecs.Contains(videoCodec) ||
                              caps.VideoCodecs.Any(c => NormalizeCodecName(c) == videoCodec);

        if (!codecSupported)
            return false;

        // Audio must be compatible (or we can copy it since HLS supports AAC/AC3)
        var audioSupported = DirectPlayAudioCodecs.Contains(audioCodec) ||
                              caps.AudioCodecs.Any(c => NormalizeCodecName(c) == audioCodec);

        if (!audioSupported)
            return false;

        // HDR requires HDR client (can't tonemap in remux, that's transcode)
        if (isHdr && !caps.SupportsHdr)
            return false;

        // Resolution must be within limits
        if (caps.MaxResolution > 0 && resolution > caps.MaxResolution)
            return false;

        return true;
    }

    private StreamPlan CreateDirectPlayPlan(Guid mediaId, MediaItem item, string videoCodec, string audioCodec, string container, bool isHdr, int resolution, string token)
    {
        var url = $"/api/v1/stream/{mediaId}?token={token}";
        var resolutionStr = resolution > 0 ? $"{resolution}p" : "Unknown";
        var hdrStr = isHdr ? " HDR" : "";

        return new StreamPlan
        {
            Method = PlaybackMethod.DirectPlay,
            Url = url,
            DisplayProfile = $"{resolutionStr}{hdrStr} {videoCodec.ToUpperInvariant()} (Direct Play)",
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            Container = container,
            IsHdr = isHdr,
            Resolution = $"{resolution}p",
            AudioChannels = 2, // Will be determined by actual stream
            Reason = "Container and codecs are natively supported by client"
        };
    }

    private StreamPlan CreateRemuxPlan(Guid mediaId, MediaItem item, string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps, string token)
    {
        var url = $"/api/transcode/{mediaId}/master.m3u8?token={token}";
        var resolutionStr = resolution > 0 ? $"{resolution}p" : "Unknown";
        var hdrStr = isHdr ? " HDR" : "";

        return new StreamPlan
        {
            Method = PlaybackMethod.Remux,
            Url = url,
            DisplayProfile = $"{resolutionStr}{hdrStr} {videoCodec.ToUpperInvariant()} (Remux)",
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            Container = "hls",
            IsHdr = isHdr,
            Resolution = $"{resolution}p",
            AudioChannels = Math.Min(caps.MaxAudioChannels, 6),
            Reason = "Codecs supported, container requires remuxing to HLS"
        };
    }

    private StreamPlan CreateTranscodePlan(Guid mediaId, MediaItem item, ClientCapabilities caps, string token, string reason)
    {
        // Determine output resolution based on client's max or keep original
        var targetResolution = caps.MaxResolution > 0 ? $"{caps.MaxResolution}p" : "1080p";

        // Use H.264 as universal fallback, prefer HEVC/AV1 if client supports
        var targetVideoCodec = "h264";
        if (caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "hevc"))
        {
            targetVideoCodec = "hevc";
        }
        else if (caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "av1"))
        {
            targetVideoCodec = "av1";
        }

        // Audio: use AAC stereo as default, or AC3 for surround
        var targetAudioCodec = "aac";
        var targetChannels = 2;
        if (caps.MaxAudioChannels >= 6 && caps.AudioCodecs.Any(c => NormalizeCodecName(c) == "ac3" || NormalizeCodecName(c) == "eac3"))
        {
            targetAudioCodec = "ac3";
            targetChannels = 6;
        }

        var url = $"/api/transcode/{mediaId}/master.m3u8?token={token}&resolution={targetResolution}";

        return new StreamPlan
        {
            Method = PlaybackMethod.Transcode,
            Url = url,
            DisplayProfile = $"{targetResolution} {targetVideoCodec.ToUpperInvariant()} (Transcode)",
            VideoCodec = targetVideoCodec,
            AudioCodec = targetAudioCodec,
            Container = "hls",
            IsHdr = false, // Transcoding always outputs SDR (tonemapping)
            Resolution = targetResolution,
            AudioChannels = targetChannels,
            Reason = reason
        };
    }

    private string DetermineTranscodeReason(string videoCodec, string audioCodec, string container, bool isHdr, int resolution, ClientCapabilities caps)
    {
        var reasons = new List<string>();

        if (!DirectPlayVideoCodecs.Contains(videoCodec) && !caps.VideoCodecs.Any(c => NormalizeCodecName(c) == videoCodec))
            reasons.Add($"Video codec '{videoCodec}' not supported");

        if (!DirectPlayAudioCodecs.Contains(audioCodec) && !caps.AudioCodecs.Any(c => NormalizeCodecName(c) == audioCodec))
            reasons.Add($"Audio codec '{audioCodec}' not supported");

        if (isHdr && !caps.SupportsHdr)
            reasons.Add("HDR content requires tonemapping for SDR client");

        if (caps.MaxResolution > 0 && resolution > caps.MaxResolution)
            reasons.Add($"Resolution {resolution}p exceeds client max {caps.MaxResolution}p");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Transcoding required";
    }

    private static string NormalizeCodecName(string codec)
    {
        if (string.IsNullOrEmpty(codec)) return "";

        var lower = codec.ToLowerInvariant().Trim();

        // Normalize common variations
        return lower switch
        {
            "h.264" or "avc1" => "h264",
            "h.265" or "hevc" => "hevc",
            "h265" => "hevc",
            "vp09" => "vp9",
            "av01" => "av1",
            "dca" or "dts-hd" => "dts",
            "eac-3" => "eac3",
            "mp4a" => "aac",
            _ => lower
        };
    }

    private static string NormalizeContainerName(string container)
    {
        if (string.IsNullOrEmpty(container)) return "";

        var lower = container.ToLowerInvariant().Trim();
        return lower switch
        {
            "matroska" or "matroska,webm" => "mkv",
            "mov,mp4,m4a,3gp,3g2,mj2" => "mp4",
            "mp4" or "m4v" => "mp4",
            _ => lower
        };
    }

    private static int ParseResolutionHeight(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution)) return 0;

        // Parse "1920x1080" format
        var parts = resolution.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[1], out var height))
            return height;

        return 0;
    }

    private static bool IsHdrContent(string? pixelFormat, string? colorTransfer)
    {
        // Check explicit HDR transfer functions
        if (!string.IsNullOrEmpty(colorTransfer))
        {
            var tf = colorTransfer.ToLowerInvariant();
            if (tf is "smpte2084" or "arib-std-b67") // PQ (HDR10) or HLG
                return true;
        }

        // Check 10-bit+ pixel formats (less reliable without transfer function)
        if (!string.IsNullOrEmpty(pixelFormat))
        {
            var fmt = pixelFormat.ToLowerInvariant();
            if (fmt.Contains("10") || fmt.Contains("12") || fmt.Contains("p010") || fmt.Contains("p016"))
            {
                // Only consider 10-bit as HDR if we don't have explicit SDR transfer
                if (string.IsNullOrEmpty(colorTransfer) || colorTransfer.ToLowerInvariant() != "bt709")
                    return true;
            }
        }

        return false;
    }
}
