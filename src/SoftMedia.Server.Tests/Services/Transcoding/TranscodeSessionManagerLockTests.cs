using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// R-WI-005 regression — a session-restarting far-seek removes the session (and disposes its
/// per-key semaphore, by the L-25 security design) while the acquiring lock is still held.
/// SessionLock.Dispose() must tolerate the already-disposed semaphore; before the fix this
/// surfaced as an ObjectDisposedException → HTTP 500 on every far-seek that restarts a transcode
/// (confirmed live).
public class TranscodeSessionManagerLockTests
{
    [Fact]
    public async Task SessionLock_Dispose_ToleratesSemaphoreDisposedByRemove()
    {
        var mgr = new TranscodeSessionManager();
        var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "sid-1");

        var sessionLock = await mgr.AcquireLockAsync(key);

        // Simulate the restart path: the session and its semaphore are removed/disposed while the
        // lock is still held (TryRemoveSession disposes the per-key semaphore).
        mgr.TryRemoveSession(key, out _);

        // Releasing the now-disposed semaphore must be a silent no-op, not a throw.
        var ex = Record.Exception(() => sessionLock.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task SessionLock_Dispose_ReleasesNormally_WhenSessionNotRemoved()
    {
        var mgr = new TranscodeSessionManager();
        var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "sid-2");

        // Acquire, release, then re-acquire without deadlock — proves the normal release path
        // still works (Dispose actually releases the semaphore).
        var first = await mgr.AcquireLockAsync(key);
        first.Dispose();

        var second = await mgr.AcquireLockAsync(key); // would hang if Dispose hadn't released
        second.Dispose();
    }

    [Fact]
    public async Task AcquireLock_PreservesMutualExclusion_AcrossSessionRemoval()
    {
        // diff-review MEDIUM: a second same-key request must WAIT for the first to release, even
        // when the session is removed while the first lock is held (the far-seek restart path).
        // Before the lifetime fix, removal disposed the in-use semaphore, which either orphaned
        // the waiter (hang) or let it acquire a fresh semaphore concurrently (lost mutual exclusion).
        var mgr = new TranscodeSessionManager();
        var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "sid-3");

        var first = await mgr.AcquireLockAsync(key);

        var secondTask = mgr.AcquireLockAsync(key);
        Assert.False(secondTask.IsCompleted, "second acquire must block while the first is held");

        // Simulate the restart: session removed while the first lock is still held.
        mgr.TryRemoveSession(key, out _);
        Assert.False(secondTask.IsCompleted, "removing the session must not free the held lock");

        first.Dispose();                                  // release
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5)); // must now complete, not hang
        second.Dispose();
    }
}
