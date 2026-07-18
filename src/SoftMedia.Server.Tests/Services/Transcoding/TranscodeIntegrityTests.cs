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
    public async Task SessionLockTable_IsBounded_ByIdlePruning()
    {
        // audit wave-2 L-25 (revised, diff-review 2026-07-16): the lock table must stay bounded as
        // an attacker cycles distinct ?sid= values — but WITHOUT disposing a lock that may still be
        // held/awaited (that disposal was the root cause of the far-seek ObjectDisposedException and
        // a lost-mutual-exclusion window). TryRemoveSession no longer evicts immediately; instead
        // provably-idle locks are pruned once the table exceeds its cap.
        var mgr = new TranscodeSessionManager();

        var locks = (IDictionary)typeof(TranscodeSessionManager)
            .GetField("_sessionLocks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(mgr)!;

        for (int i = 0; i < 1000; i++)
        {
            var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "sid" + i);
            using (await mgr.AcquireLockAsync(key)) { /* create + release the idle lock */ }
            mgr.TryRemoveSession(key, out _);
        }

        // Bounded despite 1000 distinct sids (cap is 256).
        Assert.True(locks.Count <= 256, $"lock table not bounded: {locks.Count}");

        // Re-acquiring after pruning must still work (no disposed-semaphore deadlock).
        var again = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, "again");
        using (await mgr.AcquireLockAsync(again)) { }
    }
}
