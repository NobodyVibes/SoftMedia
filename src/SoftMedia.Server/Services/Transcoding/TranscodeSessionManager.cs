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

    // Bounds the per-key lock table (audit wave-2 L-25). We can't tie the lock's lifetime to the
    // session by disposing it in TryRemoveSession: a far-seek DELETE or an in-lock restart removes
    // the session while another request for the same key still holds or awaits that lock, so
    // disposing it orphaned queued waiters (silent hang) and removing it broke mutual exclusion
    // (a fresh semaphore for a still-held key). Instead the table is pruned of provably-idle locks.
    private const int MaxIdleLocks = 256;

    public bool TryRemoveSession(TranscodeSessionKey key, out TranscodeSession? session)
    {
        var removed = _activeSessions.TryRemove(key, out session);
        PruneIdleLocks();
        return removed;
    }

    public async Task<IDisposable> AcquireLockAsync(TranscodeSessionKey key)
    {
        // Retry loop: PruneIdleLocks may dispose a semaphore between our GetOrAdd and WaitAsync
        // (only ever an idle one — never one with a holder/waiter). On that narrow race, drop the
        // exact disposed instance and re-acquire a fresh one.
        while (true)
        {
            var semaphore = _sessionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            try
            {
                await semaphore.WaitAsync();
                return new SessionLock(semaphore);
            }
            catch (ObjectDisposedException)
            {
                _sessionLocks.TryRemove(new KeyValuePair<TranscodeSessionKey, SemaphoreSlim>(key, semaphore));
            }
        }
    }

    /// Evicts only locks that are provably at rest — <c>CurrentCount == 1</c> means the
    /// <c>SemaphoreSlim(1,1)</c> has no holder and no queued waiter (either would leave it at 0) —
    /// and whose session is already gone. Never touches a lock in use, so it cannot orphan a
    /// waiter or break mutual exclusion. Runs only when the table exceeds the cap.
    private void PruneIdleLocks()
    {
        if (_sessionLocks.Count <= MaxIdleLocks) return;
        foreach (var kv in _sessionLocks)
        {
            if (kv.Value.CurrentCount == 1 && !_activeSessions.ContainsKey(kv.Key))
            {
                // Value-matched removal: only drop this exact instance, not one a concurrent
                // caller just recreated for the same key.
                if (_sessionLocks.TryRemove(kv))
                {
                    try { kv.Value.Dispose(); } catch { /* a racing acquirer retries on dispose */ }
                }
            }
        }
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
            if (_disposed) return;
            _disposed = true;

            // The lock's body may have removed the session (a seek/param-change restart calls
            // TryRemoveSession, which DISPOSES this key's semaphore by design — see the L-25 note
            // above). Releasing a disposed semaphore is a no-op that would otherwise surface as a
            // 500 on every session-restarting far-seek (the exact R-WI-005 flow); swallow it.
            try { _semaphore.Release(); }
            catch (ObjectDisposedException) { /* session already gone; nothing to release */ }
        }
    }
}
