namespace SoftMedia.Server.DTOs;

/// <summary>
/// Client capabilities for stream negotiation.
/// The frontend sends this to the backend to describe what the client can play.
/// </summary>
public class ClientCapabilities
{
    /// <summary>
    /// Video codecs the client can decode (e.g., "h264", "hevc", "av1", "vp9").
    /// </summary>
    public string[] VideoCodecs { get; set; } = ["h264"];

    /// <summary>
    /// Audio codecs the client can decode (e.g., "aac", "ac3", "eac3", "opus").
    /// </summary>
    public string[] AudioCodecs { get; set; } = ["aac"];

    /// <summary>
    /// Maximum audio channels the client supports (2 = stereo, 6 = 5.1, 8 = 7.1).
    /// </summary>
    public int MaxAudioChannels { get; set; } = 2;

    /// <summary>
    /// Whether the client supports HDR playback (Display + Codec).
    /// </summary>
    public bool SupportsHdr { get; set; } = false;

    /// <summary>
    /// Whether the display hardware reports HDR support.
    /// </summary>
    public bool DisplaySupportsHdr { get; set; } = false;

    /// <summary>
    /// Whether the browser has software support for HDR codecs.
    /// </summary>
    public bool CodecSupportsHdr { get; set; } = false;

    /// <summary>
    /// Maximum bitrate the client can handle (in kbps). 0 = unlimited.
    /// </summary>
    public int MaxBitrate { get; set; } = 0;

    /// <summary>
    /// Maximum resolution height the client prefers (e.g., 720, 1080, 2160). 0 = original.
    /// </summary>
    public int MaxResolution { get; set; } = 0;

    /// <summary>
    /// Subtitle formats the client supports for sidecar delivery (e.g., "vtt", "ass").
    /// </summary>
    public string[] SupportedSubtitleFormats { get; set; } = ["vtt"];

    /// <summary>
    /// Container formats the client supports (e.g., "mp4", "webm", "mkv", "hls").
    /// </summary>
    public string[] SupportedContainers { get; set; } = ["mp4", "webm"];
    
    /// <summary>
    /// User-requested quality preference from player UI (e.g., "auto", "720p", "1080p", "4k", "original").
    /// This overrides default streaming quality when specified.
    /// </summary>
    public string? RequestedQuality { get; set; } = null;

    /// <summary>
    /// Index of the subtitle track to be burned in (if any).
    /// </summary>
    public int? SubtitleTrackIndex { get; set; } = null;

    /// <summary>
    /// Unique identifier for this specific playback stream (to isolate concurrent sessions).
    /// </summary>
    public string? StreamId { get; set; } = null;
}


/// <summary>
/// Available playback methods, ordered by preference (less server work = better).
/// </summary>
public enum PlaybackMethod
{
    /// <summary>File is directly playable by client without any server processing.</summary>
    DirectPlay = 0,
    
    /// <summary>Streams are copied to a compatible container (no re-encoding).</summary>
    Remux = 1,
    
    /// <summary>Full transcoding required (re-encoding video/audio).</summary>
    Transcode = 2
}

/// <summary>
/// The server's decision on how to stream a specific media item to the client.
/// </summary>
public class StreamPlan
{
    /// <summary>
    /// The playback method chosen by the server.
    /// </summary>
    public PlaybackMethod Method { get; set; }

    /// <summary>
    /// The URL to use for playback.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable profile description (e.g., "4K HDR Direct Play", "1080p H.264 Transcode").
    /// </summary>
    public string DisplayProfile { get; set; } = string.Empty;

    /// <summary>
    /// The video codec that will be delivered.
    /// </summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>
    /// The audio codec that will be delivered.
    /// </summary>
    public string AudioCodec { get; set; } = string.Empty;

    /// <summary>
    /// The container format that will be delivered.
    /// </summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>
    /// Whether the output will be HDR (true) or tonemapped to SDR (false).
    /// </summary>
    public bool IsHdr { get; set; } = false;

    /// <summary>
    /// Whether the source file is HDR.
    /// </summary>
    public bool SourceIsHdr { get; set; } = false;

    /// <summary>
    /// The audio channel count that will be delivered.
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>
    /// The resolution that will be delivered (e.g., "1920x1080").
    /// </summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>
    /// Reason for choosing this playback method (for debugging/logging).
    /// Free-form English; kept for back-compat and server logs.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Structured, machine-readable reasons (P2-WI-002). The client translates each
    /// <see cref="StreamReasonCode.Code"/> to a localized human sentence for the
    /// "Why is this playing this way?" panel. Parallel to <see cref="Reason"/> so
    /// no English parsing is needed client-side.
    /// </summary>
    public List<StreamReasonCode> ReasonCodes { get; set; } = new();

    // --- Resolved transcode parameters (R-WI-002) ---
    // The authoritative quality/security params the server negotiated for a Transcode plan.
    // Persisted per session (mediaId+userId+sid) in the stream-plan store so a later
    // master.m3u8 request — notably a far-seek that rebuilds the URL with minimal params —
    // cannot silently drop the resolution/codec/HDR decision or bypass the per-user bitrate
    // cap. Null for DirectPlay/Remux (no transcode encode). Not serialized to the client.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TranscodeResolution { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? TranscodeCodec { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int? TranscodeMaxBitrate { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool TranscodePreserveHdr { get; set; }

    // Resolved audio decision (R-WI-004). The transcode path used to force stereo AAC 128k on every
    // branch; these carry the negotiated ladder — copy the source audio when the client can decode
    // it, else encode to the target codec/channels (AC3 5.1 for a surround-capable client), else
    // stereo AAC. Persisted alongside the video params so a far-seek re-request keeps them.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TranscodeAudioCopy { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? TranscodeAudioCodec { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int TranscodeAudioChannels { get; set; }
}

/// <summary>
/// A single machine-readable playback-decision reason. <see cref="Code"/> is a
/// stable dotted key (e.g. "video.codec.unsupported"); <see cref="Params"/> carries
/// the interpolation values (codec names, resolutions, bitrates) so the client can
/// localize without parsing English.
/// </summary>
public class StreamReasonCode
{
    public string Code { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();

    public StreamReasonCode() { }
    public StreamReasonCode(string code) { Code = code; }
    public StreamReasonCode(string code, Dictionary<string, string> @params) { Code = code; Params = @params; }
}

/// <summary>Canonical reason codes emitted by the stream planner.</summary>
public static class StreamReasonCodes
{
    public const string DirectPlaySupported = "directplay.supported";
    public const string RemuxContainer = "remux.container";
    public const string VideoCodecUnsupported = "video.codec.unsupported";
    public const string AudioCodecUnsupported = "audio.codec.unsupported";
    public const string HdrTonemap = "hdr.tonemap";
    public const string ResolutionExceedsMax = "resolution.exceeds-max";
    public const string TranscodeRequired = "transcode.required";
    public const string BitrateClamped = "bitrate.clamped";
    /// B-01 — the source's original bitrate exceeds the effective cap, so the
    /// original-bitrate paths (direct play / remux) are refused and playback
    /// transcodes with `-maxrate` instead.
    public const string BitrateCapForcesTranscode = "bitrate.cap-forces-transcode";
}
