using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public interface ITranscodeSessionService
{
    /// <summary>
    /// Updates the client's playback position for throttling purposes.
    /// </summary>
    void UpdateClientPosition(Guid mediaId, Guid userId, int? sub, string segment, string? sid = null);

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
    /// Stops a specific transcode session.
    /// </summary>
    void StopSession(Guid mediaId, Guid userId, int? sub, string? sid = null);

    /// <summary>
    /// Stops all transcode sessions for a user and media item.
    /// </summary>
    void StopAllSessions(Guid mediaId, Guid userId);
}

public enum TranscodeSessionResult
{
    Success,
    NotFound,
    Unauthorized
}
