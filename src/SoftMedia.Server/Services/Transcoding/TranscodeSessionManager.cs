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
        var removed = _activeSessions.TryRemove(key, out session);

        // Security (audit wave-2 L-25): tie the per-session lock's lifetime to the session so the
        // lock table can't grow without bound as an attacker cycles distinct ?sid= values. Disposed
        // only when the session is gone; a later op for the same key simply re-creates it via
        // GetOrAdd. We dispose the semaphore to release its handle.
        if (_sessionLocks.TryRemove(key, out var lockObj))
        {
            try { lockObj.Dispose(); } catch { /* a concurrent waiter may race; safe to ignore */ }
        }

        return removed;
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
