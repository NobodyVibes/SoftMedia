using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Extensions;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Regression guard for the webhook SSRF-via-redirect fix: the named "Webhooks"
/// HttpClient MUST NOT auto-follow redirects, because the worker SSRF-validates the
/// target IPs before sending — a transparently-followed 3xx could reach an internal
/// address (169.254.169.254 / 127.0.0.1 / RFC1918) the guard never saw.
public class WebhookRedirectPolicyTests
{
    [Fact]
    public void WebhooksHttpClient_DoesNotAutoFollowRedirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // AddMediaServices registers the named "Webhooks" client + its primary handler.
        services.AddMediaServices();
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        // Resolving the handler chain is how we observe AllowAutoRedirect.
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = handlerFactory.CreateHandler("Webhooks");

        // Walk to the primary SocketsHttpHandler at the end of the delegating chain.
        HttpMessageHandler current = handler;
        while (current is DelegatingHandler dh && dh.InnerHandler != null)
            current = dh.InnerHandler;

        var sockets = Assert.IsType<SocketsHttpHandler>(current);
        Assert.False(sockets.AllowAutoRedirect, "Webhooks client must have AllowAutoRedirect=false (SSRF guard).");
    }
}
