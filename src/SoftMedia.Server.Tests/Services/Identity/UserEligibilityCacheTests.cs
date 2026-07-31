using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

/// AA-WI-011 — the eligibility cache collapses the per-request media/cast-token DB
/// recheck (audit L-3) to one query per user per TTL window. Correctness hinges on:
/// verdicts round-trip within the TTL, expire after it, and eager invalidation (called
/// by every ban/delete/approve write path) drops the verdict immediately.
public class UserEligibilityCacheTests
{
    [Fact]
    public void Set_ThenTryGet_ReturnsVerdictWithinTtl()
    {
        var cache = new UserEligibilityCache();
        var userId = Guid.NewGuid();

        cache.Set(userId, eligible: true);

        Assert.True(cache.TryGet(userId, out var eligible));
        Assert.True(eligible);
    }

    [Fact]
    public void NegativeVerdicts_AreCachedToo()
    {
        var cache = new UserEligibilityCache();
        var userId = Guid.NewGuid();

        cache.Set(userId, eligible: false);

        Assert.True(cache.TryGet(userId, out var eligible));
        Assert.False(eligible);
    }

    [Fact]
    public void TryGet_UnknownUser_Misses()
    {
        var cache = new UserEligibilityCache();
        Assert.False(cache.TryGet(Guid.NewGuid(), out _));
    }

    [Fact]
    public void Invalidate_DropsTheVerdictImmediately()
    {
        var cache = new UserEligibilityCache();
        var userId = Guid.NewGuid();
        cache.Set(userId, eligible: true);

        cache.Invalidate(userId);

        Assert.False(cache.TryGet(userId, out _), "an invalidated verdict must force a DB re-read");
    }

    [Fact]
    public void ExpiredEntries_Miss()
    {
        // Ttl zero → the entry's expiry equals its write time, so it is already stale.
        var cache = new UserEligibilityCache { Ttl = TimeSpan.Zero };
        var userId = Guid.NewGuid();
        cache.Set(userId, eligible: true);

        Assert.False(cache.TryGet(userId, out _), "a stale verdict must not be served");
    }
}
