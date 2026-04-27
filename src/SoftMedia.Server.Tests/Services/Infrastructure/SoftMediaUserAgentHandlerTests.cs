using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// SDD §4.3 / §6.2 — every outbound HTTP request to a third-party metadata or
/// image host must carry a descriptive SoftMedia User-Agent. The handler
/// enforces the contract by clearing whatever the caller set and re-adding the
/// canonical UA. C2 of the 2026-04-26 hardening plan adds a named "ImageProxy"
/// HttpClient that uses this handler — these tests cover both the handler
/// itself and the named-client wiring.
public class SoftMediaUserAgentHandlerTests
{
    [Fact]
    public async Task Handler_Sets_SoftMediaUserAgent()
    {
        // Pipeline: SoftMediaUserAgentHandler -> capturing terminator.
        var captor = new CapturingHandler();
        var handler = new SoftMediaUserAgentHandler { InnerHandler = captor };
        var client = new HttpClient(handler);

        // Caller tries to spoof a browser UA — the handler must override it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (spoofed)");
        await client.GetAsync("https://example.invalid/");

        Assert.NotNull(captor.LastRequest);
        var ua = string.Join(" ", captor.LastRequest!.Headers.GetValues("User-Agent"));
        Assert.Contains("SoftMedia/", ua);
        Assert.DoesNotContain("Mozilla", ua);
    }

    [Fact]
    public async Task NamedImageProxyClient_PipelineAttachesSoftMediaHandler()
    {
        // Mirror the production wiring (ServiceCollectionExtensions adds the
        // handler to the "ImageProxy" client's pipeline) and verify that an
        // outbound request from a factory-resolved client carries the
        // SoftMedia User-Agent. We capture the outbound request by overriding
        // the primary terminal handler with a shared CapturingHandler instance.
        var captor = new CapturingHandler();

        var services = new ServiceCollection();
        services.AddTransient<SoftMediaUserAgentHandler>();
        services.AddHttpClient("ImageProxy", c => c.Timeout = TimeSpan.FromSeconds(15))
                .AddHttpMessageHandler<SoftMediaUserAgentHandler>()
                .ConfigurePrimaryHttpMessageHandler(() => captor);

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ImageProxy");

        await client.GetAsync("https://example.invalid/");

        Assert.NotNull(captor.LastRequest);
        var ua = string.Join(" ", captor.LastRequest!.Headers.GetValues("User-Agent"));
        Assert.Contains("SoftMedia/", ua);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
