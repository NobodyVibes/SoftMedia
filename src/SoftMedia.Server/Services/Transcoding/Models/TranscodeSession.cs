using System.Diagnostics;

namespace SoftMedia.Server.Services.Transcoding.Models;

/// <summary>
/// Key for tracking unique transcode sessions (mediaId + userId + subtitle track combination)
/// </summary>
public record TranscodeSessionKey(Guid MediaId, Guid UserId, int? SubtitleTrackIndex, string? StreamId = null);

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
    Completed,
    /// <summary>SR-WI-020: FFmpeg exited abnormally and crash retries are exhausted.
    /// Playlist/segment requests return 409 so the client can show a real error
    /// instead of buffering forever.</summary>
    Failed
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

    /// <summary>
    /// SR-WI-020: segment index at the moment of the last crash. The retry budget resets
    /// only once transcoding progresses meaningfully PAST this point — resetting on mere
    /// client activity (the old behavior) let a crash-looping source retry forever.
    /// </summary>
    public int LastCrashSegmentIndex { get; set; } = -1;
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
    /// Whether the source media is HDR
    /// </summary>
    public bool IsSourceHdr { get; set; }
    
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

    /// <summary>
    /// True when the negotiated plan is Remux — the compatible A/V streams are copied into the
    /// HLS container (<c>-c copy</c>) rather than re-encoded (R-WI-003). Set from the persisted
    /// stream plan; a switch between remux and transcode counts as a parameter change (restart).
    /// </summary>
    public bool IsRemux { get; set; } = false;

    /// <summary>
    /// R-WI-004 audio decision, resolved from the negotiated plan. When <see cref="AudioCopy"/> is
    /// true the source audio is stream-copied (<c>-c:a copy</c>, preserving surround); otherwise it
    /// is encoded to <see cref="AudioCodec"/> at <see cref="AudioChannels"/> channels. Replaces the
    /// old hard-coded stereo AAC 128k. Default (all unset) reproduces stereo AAC for sid-less
    /// requests that carry no plan.
    /// </summary>
    public bool AudioCopy { get; set; } = false;
    public string? AudioCodec { get; set; }
    public int AudioChannels { get; set; }

    /// <summary>
    /// Which client is playing this session, for the admin Now-Playing dashboard: a coarse
    /// form factor from the User-Agent plus the client address. Refreshed on each playlist /
    /// segment request rather than only at creation, so a session resumed from a different
    /// device shows the one playing NOW. Null until the first request that carries a
    /// HttpContext (e.g. a session restored without one).
    /// </summary>
    public Sessions.ClientDevice? ClientDevice { get; set; }
}
