using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public interface ITranscodeSessionService
{
    /// <summary>
    /// Updates the client's playback position for throttling purposes.
    /// </summary>
    void UpdateClientPosition(Guid mediaId, Guid userId, int? sub, string segment, string? sid = null);

    /// <summary>
    /// Records which client is driving this transcode (form factor + address) for the admin
    /// Now-Playing dashboard. Called per playlist/segment request so the value tracks the
    /// device playing NOW; a no-op when the session isn't live.
    /// </summary>
    void SetClientDevice(Guid mediaId, Guid userId, int? sub, string? sid, Sessions.ClientDevice device);

    /// <summary>
    /// Pauses the transcode session.
    /// </summary>
    /// <returns>True if successful, false if session not found or unauthorized.</returns>
    TranscodeSessionResult PauseSession(Guid mediaId, Guid userId, int? sub, string? sid = null);

    /// <summary>
    /// Resumes the transcode session.
    /// </summary>
    /// <returns>True if successful, false if session not found or unauthorized.</returns>
    TranscodeSessionResult ResumeSession(Guid mediaId, Guid userId, int? sub, string? sid = null);

    /// <summary>
    /// Stops a specific transcode session. By default the segments are RETAINED (the
    /// session goes dormant) so playback can resume quickly within the configured
    /// retention window; they are deleted immediately only when retention is 0.
    /// </summary>
    Task StopSession(Guid mediaId, Guid userId, int? sub, string? sid = null);

    /// <summary>
    /// Stops all transcode sessions for a user and media item (segments retained unless
    /// retention is 0). See <see cref="StopSession"/>.
    /// </summary>
    Task StopAllSessions(Guid mediaId, Guid userId);
}

public enum TranscodeSessionResult
{
    Success,
    NotFound,
    Unauthorized
}
