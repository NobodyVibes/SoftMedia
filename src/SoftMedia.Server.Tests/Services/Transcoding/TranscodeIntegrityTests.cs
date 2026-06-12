using System.Collections;
using System.Reflection;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// Audit wave-2 WS-7 — transcode session integrity: the client-supplied ?sid= must be a safe
/// opaque token (M-4, no directory traversal / unbounded growth), and per-session locks must be
/// evicted when the session ends (L-25, no unbounded lock-table growth).
public class TranscodeIntegrityTests
{
    [Theory]
    [InlineData(null)]            // "no explicit session" — allowed
    [InlineData("")]             // empty — allowed
    [InlineData("abc123")]
    [InlineData("a-b_c-D9")]
    [InlineData("0f8e7d6c5b4a39281706")]
    public void IsValid_AcceptsSafeTokens(string? sid) => Assert.True(TranscodeSid.IsValid(sid));

    [Theory]
    [InlineData("../etc")]        // traversal
    [InlineData("a/b")]           // path separator
    [InlineData("a\\b")]          // windows separator
    [InlineData("a.b")]           // dot (not in charset)
    [InlineData("a b")]           // space
    [InlineData("a;b")]           // shell-ish
    [InlineData("..")]
    public void IsValid_RejectsUnsafeTokens(string sid) => Assert.False(TranscodeSid.IsValid(sid));

    [Fact]
    public void IsValid_RejectsOverLongToken()
    {
        Assert.True(TranscodeSid.IsValid(new string('a', TranscodeSid.MaxLength)));
        Assert.False(TranscodeSid.IsValid(new string('a', TranscodeSid.MaxLength + 1)));
    }

    [Fact]
    public async Task TryRemoveSession_EvictsTheSessionLock()
    {
        var mgr = new TranscodeSessionManager();
        var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "sid1");

        using (await mgr.AcquireLockAsync(key)) { /* create + release the lock entry */ }

        var locks = (IDictionary)typeof(TranscodeSessionManager)
            .GetField("_sessionLocks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(mgr)!;
        Assert.True(locks.Count >= 1, "lock entry should exist after AcquireLockAsync");

        mgr.TryRemoveSession(key, out _);
        Assert.Empty(locks); // audit wave-2 L-25: evicted on session removal

        // Re-acquiring the same key after eviction must still work (no disposed-semaphore deadlock).
        using (await mgr.AcquireLockAsync(key)) { }
        Assert.True(locks.Count >= 1);
    }
}
