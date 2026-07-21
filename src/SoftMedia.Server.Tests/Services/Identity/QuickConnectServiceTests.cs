using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

/// NR-WI-006 — pairing-store invariants: unambiguous codes, single-use claims,
/// no approval of unknown codes, and the hard pending-store cap.
public class QuickConnectServiceTests
{
    private static QuickConnectService NewService() => new(NullLogger<QuickConnectService>.Instance);

    [Fact]
    public void Initiate_ProducesUnambiguousCode_AndLongSecret()
    {
        var init = NewService().Initiate("Living Room TV", "192.168.1.50")!;

        Assert.Equal(6, init.Code.Length);
        Assert.DoesNotContain(init.Code, c => "IO01".Contains(c)); // typed off a TV screen
        Assert.True(init.Secret.Length >= 64); // 32 random bytes, hex
        Assert.True(init.ExpiresInSeconds > 0);
    }

    [Fact]
    public void Claim_BeforeApproval_IsPending()
    {
        var svc = NewService();
        var init = svc.Initiate(null, null)!;

        var claim = svc.TryClaim(init.Secret);

        Assert.Equal(QuickConnectClaimStatus.Pending, claim.Status);
        Assert.Null(claim.UserId);
    }

    [Fact]
    public void Authorize_ThenClaim_ReturnsUser_ExactlyOnce()
    {
        var svc = NewService();
        var init = svc.Initiate("Phone", "10.0.0.9")!;
        var userId = Guid.NewGuid();

        Assert.True(svc.Authorize(init.Code, userId));

        var first = svc.TryClaim(init.Secret);
        Assert.Equal(QuickConnectClaimStatus.Approved, first.Status);
        Assert.Equal(userId, first.UserId);

        // Single-use: the entry is consumed with the successful claim.
        var second = svc.TryClaim(init.Secret);
        Assert.Equal(QuickConnectClaimStatus.NotFound, second.Status);
    }

    [Fact]
    public void Authorize_UnknownCode_Fails()
    {
        Assert.False(NewService().Authorize("ZZZZZZ", Guid.NewGuid()));
    }

    [Fact]
    public void Authorize_Twice_SecondFails()
    {
        var svc = NewService();
        var init = svc.Initiate(null, null)!;

        Assert.True(svc.Authorize(init.Code, Guid.NewGuid()));
        // A second user cannot steal an already-approved code.
        Assert.False(svc.Authorize(init.Code, Guid.NewGuid()));
    }

    [Fact]
    public void PeekPending_ShowsDeviceDetails_OnlyWhileUnapproved()
    {
        var svc = NewService();
        var init = svc.Initiate("Bedroom TV", "192.168.1.77")!;

        var pending = svc.PeekPending(init.Code)!;
        Assert.Equal("Bedroom TV", pending.DeviceName);
        Assert.Equal("192.168.1.77", pending.RequestIp);

        svc.Authorize(init.Code, Guid.NewGuid());
        Assert.Null(svc.PeekPending(init.Code)); // approved entries aren't re-reviewable
    }

    [Fact]
    public void Initiate_StoreCap_RejectsExcessPairings()
    {
        var svc = NewService();
        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(svc.Initiate($"dev-{i}", null));
        }

        Assert.Null(svc.Initiate("one-too-many", null));
    }

    [Fact]
    public void Claim_UnknownSecret_IsNotFound()
    {
        Assert.Equal(QuickConnectClaimStatus.NotFound, NewService().TryClaim(new string('a', 64)).Status);
    }
}
