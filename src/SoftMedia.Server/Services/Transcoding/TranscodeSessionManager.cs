using System.Collections.Concurrent;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public class TranscodeSessionManager : ITranscodeSessionManager
{
    private readonly ConcurrentDictionary<TranscodeSessionKey, TranscodeSession> _activeSessions = new();
    private readonly ConcurrentDictionary<TranscodeSessionKey, SemaphoreSlim> _sessionLocks = new();
    
    public IEnumerable<TranscodeSession> GetAllSessions() => _activeSessions.Values;

    public TranscodeSession? GetSession(TranscodeSessionKey key)
    {
        _activeSessions.TryGetValue(key, out var session);
        return session;
    }

    public TranscodeSession? GetSession(Guid mediaId, Guid userId, int? subtitleTrackIndex, string? sid = null)
    {
        var key = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex, sid);
        return GetSession(key);
    }

    public bool TryAddSession(TranscodeSession session)
    {
        return _activeSessions.TryAdd(session.Key, session);
    }

    public bool TryRemoveSession(TranscodeSessionKey key, out TranscodeSession? session)
    {
        return _activeSessions.TryRemove(key, out session);
    }

    public async Task<IDisposable> AcquireLockAsync(TranscodeSessionKey key)
    {
        var semaphore = _sessionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new SessionLock(semaphore);
    }

    private class SessionLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public SessionLock(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
