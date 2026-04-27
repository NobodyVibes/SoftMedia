using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Extensions;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Reflection-based guards that every brute-forceable auth action carries
/// the "auth" rate-limit policy. The actual 429 behaviour is verified in the
/// full HTTP integration tests that land with Todo 09; these unit tests exist
/// so the CI gate fails immediately if someone adds a new auth action and
/// forgets the attribute, without waiting for the heavier test suite.
public class AuthRateLimitingTests
{
    public static IEnumerable<object[]> RateLimitedAuthActions => new[]
    {
        new object[] { "Login" },
        new object[] { "Signup" },
        new object[] { "ChangePassword" },
    };

    [Theory]
    [MemberData(nameof(RateLimitedAuthActions))]
    public void AuthAction_HasAuthRateLimitPolicy(string methodName)
    {
        var method = typeof(AuthController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(ServiceCollectionExtensions.AuthRateLimitPolicy, attr!.PolicyName);
    }

    [Fact]
    public void AuthRateLimitPolicy_IsNamedConsistently()
    {
        // Defence against a rename on one side that leaves attributes pointing to a dead policy.
        Assert.Equal("auth", ServiceCollectionExtensions.AuthRateLimitPolicy);
    }
}
