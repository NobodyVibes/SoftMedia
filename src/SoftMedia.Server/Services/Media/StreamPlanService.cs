using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for determining the optimal streaming strategy based on client capabilities and source media.
/// </summary>
public interface IStreamPlanService
{
    /// <summary>
    /// Compute the optimal stream plan for a media item given client capabilities.
    /// </summary>
    /// <param name="clientIp">Resolved client IP (post forwarded-headers). Used to pick
    /// the LAN vs WAN bitrate ceiling. Null is treated as WAN (fail-safe).</param>
    /// <param name="userMaxBitrateKbps">Per-user bitrate override; when set, takes
    /// precedence over the network ceiling.</param>
    Task<StreamPlan> ComputeStreamPlanAsync(
        Guid mediaId, MediaItem mediaItem, ClientCapabilities clientCaps, string token,
        System.Net.IPAddress? clientIp = null, int? userMaxBitrateKbps = null);
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

    // Valid output codecs for transcoding (security: prevents arbitrary encoder injection)
    private static readonly HashSet<string> ValidOutputCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "h264", "hevc", "av1"
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

    // Codecs that can be *stream-copied into fMP4-HLS* (the remux container, R-WI-003). This is
    // STRICTER than direct-play eligibility: a codec can play fine in its original container yet be
    // rejected by ffmpeg's mp4/fMP4 muxer — Vorbis in particular ("Could not find tag for codec
    // vorbis"), and MP3/VP8 are unreliable in fMP4. Such sources must transcode, not remux. Kept
    // conservative on purpose; opus/flac remux can be added later once verified end-to-end.
    private static readonly HashSet<string> RemuxVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc"
    };

    private static readonly HashSet<string> RemuxAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "ac3", "eac3"
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

    public async Task<StreamPlan> ComputeStreamPlanAsync(
        Guid mediaId, MediaItem mediaItem, ClientCapabilities clientCaps, string token,
        System.Net.IPAddress? clientIp = null, int? userMaxBitrateKbps = null)
    {
        // ========== 1. Read server streaming settings ==========
        // Network-aware bitrate ceiling: a per-user override wins; otherwise pick the
        // LAN or WAN cap based on the client's network. 0 means "unlimited" for that tier.
        var isLan = NetworkClassifier.IsLan(clientIp);
        var wanBitrate = await _settingsService.GetSettingAsync("MaxStreamingBitrate", 20000);
        var lanBitrate = await _settingsService.GetSettingAsync("MaxStreamingBitrateLan", 0);

        int maxServerBitrate;
        string? bitrateClampSource;
        if (userMaxBitrateKbps is > 0)
        {
            maxServerBitrate = userMaxBitrateKbps.Value;
            bitrateClampSource = "user policy";
        }
        else if (isLan)
        {
            maxServerBitrate = lanBitrate;
            bitrateClampSource = lanBitrate > 0 ? "LAN cap" : null;
        }
        else
        {
            maxServerBitrate = wanBitrate;
            bitrateClampSource = wanBitrate > 0 ? "WAN cap" : null;
        }
        var forceDirectPlay = await _settingsService.GetSettingAsync("ForceDirectPlayWhenPossible", true);
        var defaultQuality = await _settingsService.GetSettingAsync("DefaultStreamingQuality", "auto");
        var defaultAudioChannels = await _settingsService.GetSettingAsync("DefaultAudioChannels", "auto");
        var outputCodecSetting = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
        var preserveHdrSetting = await _settingsService.GetSettingAsync("PreserveHDR", false);
        var enableAV1 = await _settingsService.GetSettingAsync("EnableAV1Encoding", false);
        var maxTranscodeResolution = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
        
        // Validate output codec against allowlist (security)
        if (!ValidOutputCodecs.Contains(outputCodecSetting))
        {
            _logger.LogWarning("Invalid OutputVideoCodec setting '{Codec}', defaulting to auto", outputCodecSetting);
            outputCodecSetting = "auto";
        }

        // ========== 2. Probe the source file ==========
        var probe = await _ffmpegService.ProbeMediaAsync(mediaItem.Path);
        
        // Fallback to DB metadata if probe failed
        if (probe == null)
        {
            bool hasDbMetadata = !string.IsNullOrEmpty(mediaItem.VideoCodec) && !string.IsNullOrEmpty(mediaItem.Container);
            if (!hasDbMetadata)
            {
                _logger.LogWarning("Could not probe media {Id} and no DB metadata available, defaulting to transcode", mediaId);
                // In absolute failure, we assume SDR H.264
                return CreateTranscodePlan(mediaId, mediaItem, SanitizeCapabilities(clientCaps), token, "Unable to probe source file", outputCodecSetting, false, false, enableAV1, mediaItem.AudioCodec ?? "", 0);
            }
            
            _logger.LogWarning("Live probe failed for {Id}, using cached DB metadata", mediaId);
            probe = new MediaProbeResult
            {
                VideoCodec = mediaItem.VideoCodec,
                AudioCodec = mediaItem.AudioCodec,
                Resolution = mediaItem.Resolution,
                PixelFormat = "yuv420p",
                Duration = mediaItem.Duration
            };
        }

        // ========== 3. Sanitize capabilities and apply overrides ==========
        var sanitizedCaps = SanitizeCapabilities(clientCaps);

        var bitrateWasClamped = false;
        if (maxServerBitrate > 0 && (sanitizedCaps.MaxBitrate <= 0 || sanitizedCaps.MaxBitrate > maxServerBitrate))
        {
            bitrateWasClamped = sanitizedCaps.MaxBitrate > maxServerBitrate;
            sanitizedCaps.MaxBitrate = maxServerBitrate;
        }
        // Annotation appended to the plan's Reason when the cap actually bit, so the
        // "Why is this transcoding?" panel can explain the bandwidth limit (Phase 2.2).
        var bitrateNote = bitrateWasClamped && bitrateClampSource != null
            ? $"Bitrate limited to {maxServerBitrate} kbps by {bitrateClampSource}."
            : null;
        // Structured parallel of the note for the client-side explainer (P2-WI-002).
        var bitrateCode = bitrateWasClamped && bitrateClampSource != null
            ? new StreamReasonCode(StreamReasonCodes.BitrateClamped, new Dictionary<string, string>
            {
                ["kbps"] = maxServerBitrate.ToString(),
                ["source"] = bitrateClampSource, // "WAN cap" | "LAN cap" | "user policy"
            })
            : null;
        
        var effectiveQuality = !string.IsNullOrEmpty(clientCaps.RequestedQuality) && clientCaps.RequestedQuality != "auto"
            ? clientCaps.RequestedQuality
            : defaultQuality;
            
        if (effectiveQuality != "auto" && effectiveQuality != "original")
        {
            var qualityResolution = ParseQualityToResolution(effectiveQuality);
            if (qualityResolution > 0)
                sanitizedCaps.MaxResolution = qualityResolution;
        }
        
        if (maxTranscodeResolution != "original" && maxTranscodeResolution != "auto")
        {
            var maxTranscodeHeight = ParseQualityToResolution(maxTranscodeResolution);
            if (maxTranscodeHeight > 0 && (sanitizedCaps.MaxResolution <= 0 || sanitizedCaps.MaxResolution > maxTranscodeHeight))
                sanitizedCaps.MaxResolution = maxTranscodeHeight;
        }
        
        if (defaultAudioChannels != "auto")
        {
            var channels = ParseAudioChannels(defaultAudioChannels);
            if (channels > 0 && sanitizedCaps.MaxAudioChannels > channels)
                sanitizedCaps.MaxAudioChannels = channels;
        }

        // ========== 4. Analyze source content ==========
        var sourceVideoCodec = NormalizeCodecName(mediaItem.VideoCodec ?? probe.VideoCodec ?? "");
        var sourceAudioCodec = NormalizeCodecName(mediaItem.AudioCodec ?? probe.AudioCodec ?? "");
        var sourceContainer = NormalizeContainerName(mediaItem.Container ?? "");
        var sourceResolution = ParseResolutionHeight(probe.Resolution);
        
        // --- HDR / Tone Mapping Handling ---
        var sourceIsHdr = IsHdrContent(probe.PixelFormat, probe.ColorTransfer);
        var forceToneMappingForSubtitles = sourceIsHdr && clientCaps.SubtitleTrackIndex.HasValue;
        
        // PreserveHDR is only effective when server enabled, client supports, AND no subtitles burning in
        var effectivePreserveHdr = preserveHdrSetting && sanitizedCaps.SupportsHdr && !forceToneMappingForSubtitles;
        
        if (preserveHdrSetting && forceToneMappingForSubtitles)
        {
            _logger.LogInformation("PreserveHDR requested but OVERRIDDEN: tone mapping forced for subtitle burn-in matching");
        }
        else if (preserveHdrSetting && !sanitizedCaps.SupportsHdr)
        {
            _logger.LogInformation("PreserveHDR requested but client doesn't support HDR - will tonemap");
        }

        _logger.LogInformation(
            "Stream plan for {Id}: Container={Container}, Video={Video}, Audio={Audio}, HDR={HDR}, Resolution={Res}, PreserveHDR={EffHDR}",
            mediaId, sourceContainer, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, effectivePreserveHdr);

        // ========== 5. Decision Logic ==========

        // Original-bitrate paths (direct play AND remux) serve the source uncapped —
        // both must be refused when the source exceeds the effective bitrate ceiling
        // so playback falls through to Transcode (which applies `-maxrate`).
        // Direct play was the last uncapped path (backlog B-01, flagged as the
        // follow-up when the remux gate landed in R-WI-003).
        var fitsCeiling = SourceFitsBitrateCeiling(probe, sanitizedCaps.MaxBitrate);

        // Check 1: Direct Play
        if (forceDirectPlay && fitsCeiling
            && CanDirectPlay(sourceContainer, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps))
        {
            var direct = CreateDirectPlayPlan(mediaId, mediaItem, sourceVideoCodec, sourceAudioCodec, sourceContainer, sourceIsHdr, sourceResolution, token);
            direct.ReasonCodes.Add(new StreamReasonCode(StreamReasonCodes.DirectPlaySupported));
            return AppendNote(direct, bitrateNote, bitrateCode);
        }

        // Check 2: Remux — same ceiling rule (a stream-copy has no `-maxrate` either).
        if (fitsCeiling &&
            CanRemux(sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps))
        {
            var remux = CreateRemuxPlan(mediaId, mediaItem, sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps, token);
            remux.ReasonCodes.Add(new StreamReasonCode(StreamReasonCodes.RemuxContainer,
                new Dictionary<string, string> { ["container"] = sourceContainer }));
            return AppendNote(remux, bitrateNote, bitrateCode);
        }

        // Fallback: Transcode
        var transcode = CreateTranscodePlan(mediaId, mediaItem, sanitizedCaps, token,
            DetermineTranscodeReason(sourceVideoCodec, sourceAudioCodec, sourceContainer, sourceIsHdr, sourceResolution, sanitizedCaps),
            outputCodecSetting, effectivePreserveHdr, sourceIsHdr, enableAV1,
            sourceAudioCodec, probe.AudioChannels ?? 0, bitrateCapped: maxServerBitrate > 0);
        transcode.ReasonCodes.AddRange(
            DetermineTranscodeReasonCodes(sourceVideoCodec, sourceAudioCodec, sourceIsHdr, sourceResolution, sanitizedCaps));
        if (!fitsCeiling)
        {
            // B-01: the debug panel should say WHY a direct-playable source transcodes.
            transcode.ReasonCodes.Add(new StreamReasonCode(StreamReasonCodes.BitrateCapForcesTranscode,
                new Dictionary<string, string> { ["capKbps"] = sanitizedCaps.MaxBitrate.ToString() }));
        }
        return AppendNote(transcode, bitrateNote, bitrateCode);
    }

    /// Appends a non-null note to the plan's Reason (space-separated) and, when present,
    /// the structured bitrate-clamp code. Surfaces the bandwidth-clamp source without
    /// disturbing the primary transcode rationale.
    private static StreamPlan AppendNote(StreamPlan plan, string? note, StreamReasonCode? code = null)
    {
        if (!string.IsNullOrEmpty(note))
            plan.Reason = string.IsNullOrEmpty(plan.Reason) ? note : $"{plan.Reason} {note}";
        if (code != null)
            plan.ReasonCodes.Add(code);
        return plan;
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
            SupportsHdr = caps.SupportsHdr,
            StreamId = caps.StreamId
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

    /// True when a stream-copy of the source would stay within <paramref name="effectiveMaxBitrateKbps"/>
    /// (the ceiling a transcode would enforce). Returns true when the source bitrate is unknown or
    /// there is effectively no cap, so we don't force a needless transcode on missing probe data.
    private static bool SourceFitsBitrateCeiling(MediaProbeResult probe, int effectiveMaxBitrateKbps)
    {
        if (probe.Bitrate is not > 0 || effectiveMaxBitrateKbps <= 0) return true;
        var sourceKbps = probe.Bitrate.Value / 1000;
        return sourceKbps <= effectiveMaxBitrateKbps;
    }

    private bool CanRemux(string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps)
    {
        // For remux we stream-copy into fMP4-HLS, so each track must be BOTH (a) copyable into that
        // container and (b) decodable by the client. (a) is the stricter test the old code missed:
        // some codecs direct-play in their native container but the fMP4 muxer rejects them (Vorbis)
        // or MSE won't decode them there (VP8) — those must transcode, not remux (R-WI-003 review).
        var videoMuxable = RemuxVideoCodecs.Contains(videoCodec);
        var videoClientPlayable = DirectPlayVideoCodecs.Contains(videoCodec) ||
                                  caps.VideoCodecs.Any(c => NormalizeCodecName(c) == videoCodec);
        if (!videoMuxable || !videoClientPlayable)
            return false;

        var audioMuxable = RemuxAudioCodecs.Contains(audioCodec);
        var audioClientPlayable = DirectPlayAudioCodecs.Contains(audioCodec) ||
                                  caps.AudioCodecs.Any(c => NormalizeCodecName(c) == audioCodec);
        if (!audioMuxable || !audioClientPlayable)
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
            Reason = "Container and codecs are natively supported by client",
            SourceIsHdr = isHdr
        };
    }

    private StreamPlan CreateRemuxPlan(Guid mediaId, MediaItem item, string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps, string token)
    {
        var sidParam = !string.IsNullOrEmpty(caps.StreamId) ? $"&sid={caps.StreamId}" : "";
        var url = $"/api/transcode/{mediaId}/master.m3u8?token={token}{sidParam}";
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
            Reason = "Codecs supported, container requires remuxing to HLS",
            SourceIsHdr = isHdr
        };
    }

    private StreamPlan CreateTranscodePlan(Guid mediaId, MediaItem item, ClientCapabilities caps, string token, string reason, string outputCodecSetting, bool preserveHdr, bool sourceIsHdr, bool enableAV1, string sourceAudioCodec = "", int sourceAudioChannels = 0, bool bitrateCapped = false)
    {
        // Determine output resolution based on client's max or keep original
        var targetResolution = caps.MaxResolution > 0 ? $"{caps.MaxResolution}p" : "1080p";

        // Determine target video codec based on setting and client support
        var targetVideoCodec = "h264"; // Universal fallback
        
        if (outputCodecSetting == "auto")
        {
            // Auto: prefer more efficient codec if client supports
            // Only consider AV1 if explicitly enabled (due to high hardware requirements)
            if (enableAV1 && caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "av1"))
            {
                targetVideoCodec = "av1";
            }
            else if (caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "hevc"))
            {
                targetVideoCodec = "hevc";
            }
        }
        else if (outputCodecSetting != "auto")
        {
            // Specific codec requested - use if client supports, else fallback
            // AV1 still requires the EnableAV1 setting even when explicitly requested
            if (outputCodecSetting == "av1" && enableAV1 && caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "av1"))
            {
                targetVideoCodec = "av1";
            }
            else if (outputCodecSetting == "hevc" && caps.VideoCodecs.Any(c => NormalizeCodecName(c) == "hevc"))
            {
                targetVideoCodec = "hevc";
            }
            else if (outputCodecSetting == "h264")
            {
                targetVideoCodec = "h264";
            }
            // else fallback to h264
        }

        // Audio ladder (R-WI-004) — previously the builder forced stereo AAC 128k regardless of this.
        //   1. COPY the source audio when the client can decode it (best quality, no CPU, preserves
        //      the source's channels — this is how surround usually survives: an AC3 5.1 source to
        //      an AC3-capable client is copied, not re-encoded).
        //   2. else ENCODE to AC3 5.1 when the client wants surround AND the source is multichannel.
        //   3. else stereo AAC.
        // Copy is only safe when the source audio can be muxed into the transcode's HLS container
        // (TS or fMP4). That is STRICTER than direct-play decodability — the same constraint as
        // remux (RemuxAudioCodecs = aac/ac3/eac3); e.g. a FLAC/Opus/Vorbis source must be encoded,
        // not copied, even though the browser could decode it in its native container.
        var normalizedSourceAudio = NormalizeCodecName(sourceAudioCodec);
        var clientPlaysSourceAudio = RemuxAudioCodecs.Contains(normalizedSourceAudio) &&
            (DirectPlayAudioCodecs.Contains(normalizedSourceAudio) ||
             caps.AudioCodecs.Any(c => NormalizeCodecName(c) == normalizedSourceAudio));

        bool audioCopy;
        string targetAudioCodec;
        int targetChannels;
        // When a per-user / network bitrate cap is in effect, prefer a BOUNDED encode over a copy:
        // a stream-copy preserves the source's original (uncapped) audio bitrate — a copied E-AC3
        // Atmos track can run ~1.5 Mbps — which would blow a capped user's budget on top of the
        // video -maxrate. Encoding caps audio at ≤448k while still preserving surround as AC3 5.1
        // (diff-review MEDIUM; mirrors the SourceFitsBitrateCeiling gate).
        if (clientPlaysSourceAudio && !bitrateCapped)
        {
            audioCopy = true;
            targetAudioCodec = normalizedSourceAudio;                     // for display; ffmpeg copies
            targetChannels = sourceAudioChannels > 0 ? sourceAudioChannels : 2;
        }
        else if (caps.MaxAudioChannels >= 6 && sourceAudioChannels >= 6 &&
                 caps.AudioCodecs.Any(c => NormalizeCodecName(c) == "ac3" || NormalizeCodecName(c) == "eac3"))
        {
            audioCopy = false;
            targetAudioCodec = "ac3";
            targetChannels = 6;
        }
        else
        {
            audioCopy = false;
            targetAudioCodec = "aac";
            targetChannels = 2;
        }

        // Build URL with codec and HDR parameters
        var sidParam = !string.IsNullOrEmpty(caps.StreamId) ? $"&sid={caps.StreamId}" : "";
        var url = $"/api/transcode/{mediaId}/master.m3u8?token={token}&resolution={targetResolution}&codec={targetVideoCodec}{sidParam}";
        
        // Add bitrate limit if present (and less than global max, otherwise no need to clutter URL)
        if (caps.MaxBitrate < MaxAllowedBitrate)
        {
            url += $"&bitrate={caps.MaxBitrate}";
        }

        // Determine if output will be HDR
        var outputIsHdr = preserveHdr && sourceIsHdr && caps.SupportsHdr;
        if (outputIsHdr)
        {
            url += "&hdr=true";
        }

        return new StreamPlan
        {
            Method = PlaybackMethod.Transcode,
            Url = url,
            DisplayProfile = $"{targetResolution} {targetVideoCodec.ToUpperInvariant()}{(outputIsHdr ? " HDR" : "")} (Transcode)",
            VideoCodec = targetVideoCodec,
            AudioCodec = targetAudioCodec,
            Container = "hls",
            IsHdr = outputIsHdr,
            Resolution = targetResolution,
            AudioChannels = targetChannels,
            Reason = reason,
            SourceIsHdr = sourceIsHdr,
            // R-WI-002: expose the resolved transcode params so the controller can persist the
            // authoritative plan. These are exactly the values encoded into the URL above.
            TranscodeResolution = targetResolution,
            TranscodeCodec = targetVideoCodec,
            TranscodeMaxBitrate = caps.MaxBitrate < MaxAllowedBitrate ? caps.MaxBitrate : null,
            TranscodePreserveHdr = outputIsHdr,
            // R-WI-004: the resolved audio decision (copy / encode codec + channels).
            TranscodeAudioCopy = audioCopy,
            TranscodeAudioCodec = targetAudioCodec,
            TranscodeAudioChannels = targetChannels
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

    /// Structured parallel of <see cref="DetermineTranscodeReason"/> — same conditions,
    /// emitted as machine-readable codes + params for the client-side explainer (P2-WI-002).
    private List<StreamReasonCode> DetermineTranscodeReasonCodes(
        string videoCodec, string audioCodec, bool isHdr, int resolution, ClientCapabilities caps)
    {
        var codes = new List<StreamReasonCode>();

        if (!DirectPlayVideoCodecs.Contains(videoCodec) && !caps.VideoCodecs.Any(c => NormalizeCodecName(c) == videoCodec))
            codes.Add(new StreamReasonCode(StreamReasonCodes.VideoCodecUnsupported,
                new Dictionary<string, string> { ["codec"] = videoCodec }));

        if (!DirectPlayAudioCodecs.Contains(audioCodec) && !caps.AudioCodecs.Any(c => NormalizeCodecName(c) == audioCodec))
            codes.Add(new StreamReasonCode(StreamReasonCodes.AudioCodecUnsupported,
                new Dictionary<string, string> { ["codec"] = audioCodec }));

        if (isHdr && !caps.SupportsHdr)
            codes.Add(new StreamReasonCode(StreamReasonCodes.HdrTonemap));

        if (caps.MaxResolution > 0 && resolution > caps.MaxResolution)
            codes.Add(new StreamReasonCode(StreamReasonCodes.ResolutionExceedsMax,
                new Dictionary<string, string> { ["resolution"] = $"{resolution}p", ["max"] = $"{caps.MaxResolution}p" }));

        if (codes.Count == 0)
            codes.Add(new StreamReasonCode(StreamReasonCodes.TranscodeRequired));

        return codes;
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
