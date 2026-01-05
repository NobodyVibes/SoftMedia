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
    /// Whether the client supports HDR playback.
    /// </summary>
    public bool SupportsHdr { get; set; } = false;

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
    /// Whether HDR will be preserved (true) or tonemapped to SDR (false).
    /// </summary>
    public bool IsHdr { get; set; } = false;

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
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
