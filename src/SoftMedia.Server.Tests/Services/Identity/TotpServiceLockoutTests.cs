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
}
