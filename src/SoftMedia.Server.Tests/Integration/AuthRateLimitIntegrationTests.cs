using System.Net;
using System.Net.Http.Json;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Acceptance test for the "auth" rate-limit policy: per-IP sliding window of
/// `AuthPermitLimit` requests per minute. The (PermitLimit + 1)-th attempt
/// from the same IP must return 429.
///
/// TestServer passes every request through the same pipeline with the same
/// RemoteIpAddress (null by default — the limiter's `"unknown-ip"` fallback
/// partition handles this deterministically).
public class AuthRateLimitIntegrationTests : IntegrationTestBase
{
    private const int Limit = ServiceCollectionExtensions.AuthPermitLimit;

    [Fact]
    public async Task Login_AttemptOverLimit_Returns429()
    {
        var client = Factory.CreateClient();
        var payload = new { Username = "nobody", Password = "wrong" };

        // First `Limit` attempts: 401 (no user "nobody" exists).
        for (var i = 0; i < Limit; i++)
        {
            var r = await client.PostAsJsonAsync("/api/v1/auth/login", payload);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        var overLimit = await client.PostAsJsonAsync("/api/v1/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    [Fact]
    public async Task Signup_AttemptOverLimit_Returns429()
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

        // First request can be 200 (first-user-setup), subsequent ones 400/403.
        for (var i = 0; i < Limit; i++)
        {
            var r = await client.PostAsJsonAsync("/api/v1/auth/signup", payload);
            Assert.True(
                r.StatusCode == HttpStatusCode.OK ||
                r.StatusCode == HttpStatusCode.BadRequest ||
                r.StatusCode == HttpStatusCode.Forbidden,
                $"Unexpected status {r.StatusCode} at attempt {i + 1}");
        }

        var overLimit = await client.PostAsJsonAsync("/api/v1/auth/signup", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }
}
