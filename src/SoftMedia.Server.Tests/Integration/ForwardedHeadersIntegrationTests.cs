using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Acceptance test for the trusted-proxy / forwarded-headers pipeline added in
/// roadmap item P0-WI-001. When SoftMedia runs behind a reverse proxy, the
/// `X-Forwarded-For` header from a trusted proxy MUST rewrite RemoteIpAddress
/// before the rate-limit middleware partitions per-IP buckets. Without that
/// rewrite, every login attempt looks like it came from the proxy's loopback
/// address and the rate limiter collapses into a single shared bucket.
///
/// TestServer's connection RemoteIpAddress is `null` by default (see the
/// inline comment in <see cref="AuthRateLimitIntegrationTests"/>), which
/// would cause ForwardedHeadersMiddleware to skip the header because no
/// origin can be matched against KnownProxies. A startup filter installed
/// only in this test factory sets RemoteIpAddress to loopback so the
/// production middleware (which trusts loopback by default) accepts
/// forwarded headers from the test client.
public class ForwardedHeadersIntegrationTests : IAsyncLifetime
{
    private ForwardedHeadersTestFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new ForwardedHeadersTestFactory();
        _ = _factory.Services;
        await _factory.ResetSeedNoiseAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RateLimiter_PartitionsByXForwardedFor_WhenProxyIsTrusted()
    {
        var client = _factory.CreateClient();
        var payload = new { Username = "nobody", Password = "wrong" };
        var limit = ServiceCollectionExtensions.AuthPermitLimit;

        // Exhaust the bucket for forwarded IP 1.2.3.4.
        for (var i = 0; i < limit; i++)
        {
            var r = await SendLoginWithForwardedIp(client, payload, "1.2.3.4");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // The (limit + 1)-th attempt from the same forwarded IP must be rate
        // limited.
        var overFirst = await SendLoginWithForwardedIp(client, payload, "1.2.3.4");
        Assert.Equal(HttpStatusCode.TooManyRequests, overFirst.StatusCode);

        // A first attempt from a different forwarded IP must NOT be in the
        // same bucket — proving the rate limiter is partitioning by the
        // forwarded address rather than the proxy loopback.
        var firstFromSecond = await SendLoginWithForwardedIp(client, payload, "5.6.7.8");
        Assert.Equal(HttpStatusCode.Unauthorized, firstFromSecond.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendLoginWithForwardedIp(
        HttpClient client, object payload, string forwardedIp)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(payload),
        };
        msg.Headers.Add("X-Forwarded-For", forwardedIp);
        return await client.SendAsync(msg);
    }

    /// Test-only factory: injects a startup filter that sets
    /// Connection.RemoteIpAddress = IPAddress.Loopback. Production
    /// ForwardedHeadersOptions trusts loopback by default, so the
    /// middleware accepts X-Forwarded-For from the test client.
    private class ForwardedHeadersTestFactory : SoftMediaWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, FakeLoopbackRemoteIpFilter>();
            });
        }
    }

    private class FakeLoopbackRemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (ctx.Connection.RemoteIpAddress is null)
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                }
                await n();
            });
            next(app);
        };
    }
}
