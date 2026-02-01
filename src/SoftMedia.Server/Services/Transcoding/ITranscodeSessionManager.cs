using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public interface ITranscodeSessionManager
{
    IEnumerable<TranscodeSession> GetAllSessions();
    TranscodeSession? GetSession(TranscodeSessionKey key);
    TranscodeSession? GetSession(Guid mediaId, Guid userId, int? subtitleTrackIndex, string? sid = null);
    bool TryAddSession(TranscodeSession session);
    bool TryRemoveSession(TranscodeSessionKey key, out TranscodeSession? session);
    Task<IDisposable> AcquireLockAsync(TranscodeSessionKey key);
}
