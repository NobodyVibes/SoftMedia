using Microsoft.Extensions.Configuration;
using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

/// Security audit M3: per-user 2FA brute-force lockout. Bounds guessing regardless of how many
/// challenge ids are minted, and is independent across users.
public class TotpServiceLockoutTests
{
    private const int Threshold = 10; // mirrors TotpService.MaxFailedAttempts

    private static ITotpService NewService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Secret"] = "unit-test-secret-value-1234567890" })
            .Build();
        return new TotpService(config);
    }

    [Fact]
    public void LocksOut_OnlyAfterThreshold()
    {
        var svc = NewService();
        var user = Guid.NewGuid();

        for (var i = 0; i < Threshold - 1; i++)
        {
            svc.RegisterFailedAttempt(user);
            Assert.False(svc.IsLockedOut(user));
        }

        svc.RegisterFailedAttempt(user); // the Nth failure arms the lockout
        Assert.True(svc.IsLockedOut(user));
    }

    [Fact]
    public void Reset_ClearsLockoutAndCounter()
    {
        var svc = NewService();
        var user = Guid.NewGuid();
        for (var i = 0; i < Threshold; i++) svc.RegisterFailedAttempt(user);
        Assert.True(svc.IsLockedOut(user));

        svc.ResetFailedAttempts(user);
        Assert.False(svc.IsLockedOut(user));
    }

    [Fact]
    public void Lockout_IsPerUser()
    {
        var svc = NewService();
        var locked = Guid.NewGuid();
        var other = Guid.NewGuid();

        for (var i = 0; i < Threshold; i++) svc.RegisterFailedAttempt(locked);

        Assert.True(svc.IsLockedOut(locked));
        Assert.False(svc.IsLockedOut(other)); // a different user is unaffected
    }

    // --- TryBeginAttempt: the atomic, race-free primitive (audit wave-2 M-3) ---

    [Fact]
    public void TryBeginAttempt_AllowsUpToThresholdMinusOne_ThenLocks()
    {
        var svc = NewService();
        var user = Guid.NewGuid();

        for (var i = 0; i < Threshold - 1; i++)
            Assert.False(svc.TryBeginAttempt(user)); // attempts 1..9 proceed

        Assert.True(svc.TryBeginAttempt(user));  // 10th attempt arms + reports lockout
        Assert.True(svc.TryBeginAttempt(user));  // subsequent attempts stay locked
        Assert.True(svc.IsLockedOut(user));
    }

    [Fact]
    public async Task TryBeginAttempt_ConcurrentCalls_NeverExceedThreshold()
    {
        // The whole point of M-3: firing many parallel attempts must NOT let more than
        // (Threshold-1) guesses through before the lockout arms — the check-then-increment race
        // previously allowed concurrent callers to slip past.
        var svc = NewService();
        var user = Guid.NewGuid();
        var proceeded = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (_, _) =>
        {
            if (!svc.TryBeginAttempt(user)) Interlocked.Increment(ref proceeded);
            await Task.CompletedTask;
        });

        Assert.Equal(Threshold - 1, proceeded); // exactly 9 proceed, regardless of concurrency
        Assert.True(svc.IsLockedOut(user));
    }

    [Fact]
    public void TryBeginAttempt_ResetClearsCounter()
    {
        var svc = NewService();
        var user = Guid.NewGuid();
        for (var i = 0; i < Threshold - 1; i++) svc.TryBeginAttempt(user);

        svc.ResetFailedAttempts(user); // a successful 2FA clears the count

        // Fresh budget after reset.
        Assert.False(svc.TryBeginAttempt(user));
        Assert.False(svc.IsLockedOut(user));
    }
}
