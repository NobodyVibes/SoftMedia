using System.Diagnostics;

namespace SoftMedia.Server.Services.Transcoding.Models;

/// <summary>
/// Key for tracking unique transcode sessions (mediaId + userId + subtitle track combination)
/// </summary>
public record TranscodeSessionKey(Guid MediaId, Guid UserId, int? SubtitleTrackIndex);

/// <summary>
/// Transcode state for throttling state machine.
/// Simplified model: FFmpeg is either actively transcoding or suspended.
/// </summary>
public enum TranscodeState
{
    /// <summary>FFmpeg is actively transcoding segments</summary>
    Transcoding,
    /// <summary>FFmpeg is suspended (paused) because buffer is full</summary>
    Throttled,
    /// <summary>User paused playback, FFmpeg stopped, segments retained</summary>
    Dormant,
    /// <summary>Session ended, cleanup complete</summary>
    Completed
}

/// <summary>
/// Represents an active transcode session with throttling state
/// </summary>
public class TranscodeSession
{
    public TranscodeSessionKey Key { get; init; } = null!;
    public Guid UserId { get; init; }
    public string InputPath { get; init; } = string.Empty;
    public Process? Process { get; set; }
    public TranscodeState State { get; set; } = TranscodeState.Transcoding;
    public bool IsSuspended { get; set; } = false;  // Tracks if FFmpeg process is currently suspended
    public double? SeekPosition { get; set; }  // Starting seek position for this session
    public int LatestSegmentIndex { get; set; } = 0;
    public int ClientSegmentIndex { get; set; } = 0;
    public DateTime LastClientRequestTime { get; set; } = DateTime.UtcNow;
    public DateTime SessionStartTime { get; init; } = DateTime.UtcNow;
    public bool IsPaused { get; set; } = false;
    public int CrashRetryCount { get; set; } = 0;
    public string SessionDirectory { get; init; } = string.Empty;
    
    /// <summary>
    /// Path to extracted WebVTT subtitle file for sidecar delivery (null if no subtitles selected)
    /// </summary>
    public string? SubtitleVttPath { get; set; }
    
    /// <summary>
    /// Language code of the selected subtitle track (for HLS manifest)
    /// </summary>
    public string? SubtitleLanguage { get; set; }
    
    /// <summary>
    /// True if the selected subtitle is bitmap-based (PGS, VOBSUB) and requires burn-in
    /// </summary>
    public bool IsBitmapSubtitle { get; set; } = false;
    
    /// <summary>
    /// Target resolution for transcoding (e.g., "720p", "1080p", "4k", "original")
    /// </summary>
    public string? TargetResolution { get; set; }
    
    /// <summary>
    /// Target video codec for transcoding (e.g., "h264", "hevc", "av1")
    /// </summary>
    public string? TargetCodec { get; set; }
    
    /// <summary>
    /// Whether to preserve HDR (skip tone mapping)
    /// </summary>
    public bool PreserveHdr { get; set; }
    
    /// <summary>
    /// Selected audio track index (null = use default audio track)
    /// </summary>
    public int? AudioTrackIndex { get; set; }

    /// <summary>
    /// Maximum bitrate limit in kbps (null = unlimited)
    /// </summary>
    public int? MaxBitrate { get; set; }

    /// <summary>
    /// Whether to force subtitle burn-in (even for text subtitles)
    /// </summary>
    public bool BurnSubtitles { get; set; } = false;
}
