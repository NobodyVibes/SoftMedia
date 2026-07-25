using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

/// <summary>
/// BG-WI-005: read-only "is anyone actually watching right now?" signal for background
/// media jobs (trickplay sweep, intro/credits detection) so they yield the machine to
/// live playback. Active means a transcode session in Transcoding or Throttled state
/// whose client requested a segment within the same 90-second inactivity window
/// ThrottleMonitorService uses before parking a session DORMANT. Direct-play streaming
/// is deliberately NOT gated: it is plain file I/O with no decode cost, and the
/// background jobs already run at BelowNormal priority (BG-WI-002).
/// </summary>
public interface IPlaybackActivityService
{
    bool IsPlaybackActive { get; }
}

public class PlaybackActivityService : IPlaybackActivityService
{
    // Mirrors ThrottleMonitorService.ClientInactivityTimeoutSeconds: past this window
    // the monitor parks the session DORMANT anyway, so it no longer represents a viewer.
    private const int ClientActivityWindowSeconds = 90;

    private readonly ITranscodeSessionManager _sessions;

    public PlaybackActivityService(ITranscodeSessionManager sessions) => _sessions = sessions;

    public bool IsPlaybackActive =>
        _sessions.GetAllSessions().Any(s =>
            s.State is TranscodeState.Transcoding or TranscodeState.Throttled
            && (DateTime.UtcNow - s.LastClientRequestTime).TotalSeconds <= ClientActivityWindowSeconds);
}
