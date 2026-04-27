using System.Net;
using System.Net.Http.Json;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Todo 03 acceptance test: the "auth" rate-limit policy must return 429 on
/// the 6th attempt from the same IP within the 1-minute fixed window.
/// Reflection tests verified the attribute is present; this suite verifies
/// the middleware actually throttles live traffic.
public class AuthRateLimitIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Login_SixthAttemptFromSameIp_Returns429()
    {
        // TestServer passes every request through the same pipeline with the
        // same RemoteIpAddress (null by default — the limiter's fallback
        // partition `"unknown-ip"` handles this deterministically).
        var client = Factory.CreateClient();
        var payload = new { Username = "nobody", Password = "wrong" };

        // Five 401s are expected (no user "nobody" exists).
        for (var i = 0; i < 5; i++)
        {
            var r = await client.PostAsJsonAsync("/api/v1/auth/login", payload);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        var sixth = await client.PostAsJsonAsync("/api/v1/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    [Fact]
    public async Task Signup_SixthAttemptFromSameIp_Returns429()
    {
        var client = Factory.CreateClient();
        var payload = new
        {
            Username = "duplicate",
            Password = "Password!1",
            InviteCode = (string?)null,
            FirstName = "D",
            LastName = "D"
        };

        // First succeeds as first-user-setup, rest fail 400 on duplicate username.
        for (var i = 0; i < 5; i++)
        {
            var r = await client.PostAsJsonAsync("/api/v1/auth/signup", payload);
            Assert.True(
                r.StatusCode == HttpStatusCode.OK ||
                r.StatusCode == HttpStatusCode.BadRequest ||
                r.StatusCode == HttpStatusCode.Forbidden,
                $"Unexpected status {r.StatusCode} at attempt {i + 1}");
        }

        var sixth = await client.PostAsJsonAsync("/api/v1/auth/signup", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }
}
