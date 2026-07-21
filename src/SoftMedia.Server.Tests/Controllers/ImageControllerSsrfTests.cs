using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// SSRF guard for the image proxy: an allowlisted host must not be able to redirect
/// the proxy to a non-allowlisted / internal address. Redirects are followed manually
/// with the allowlist re-applied on every hop.
public class ImageControllerSsrfTests : IDisposable
{
    private readonly string _webRoot;

    public ImageControllerSsrfTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "sm-imgproxy-ssrf-" + Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(_webRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Directory.GetParent(_webRoot)!.FullName, true); } catch { }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public readonly List<string> Requested = new();
        public required Func<Uri, HttpResponseMessage> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Respond(request.RequestUri!));
        }
    }

    private ImageController Build(ScriptedHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);
        var thumbs = new Mock<IThumbnailService>();
        return new ImageController(factory.Object, env.Object, NullLogger<ImageController>.Instance, thumbs.Object);
    }

    [Fact]
    public async Task Proxy_RedirectToInternalAddress_IsBlocked_AndNeverRequested()
    {
        var handler = new ScriptedHandler
        {
            Respond = uri =>
            {
                if (uri.Host == "m.media-amazon.com")
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                    r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
                    return r;
                }
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return ok;
            },
        };

        var result = await Build(handler).ProxyImage("https://m.media-amazon.com/images/poster.jpg", null);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.DoesNotContain(handler.Requested, u => u.Contains("169.254.169.254"));
    }

    [Fact]
    public async Task Proxy_RedirectToAllowlistedHost_IsFollowed_AndServed()
    {
        var handler = new ScriptedHandler
        {
            Respond = uri =>
            {
                if (uri.Host == "m.media-amazon.com")
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                    r.Headers.Location = new Uri("https://covers.openlibrary.org/cover.jpg"); // allowlisted
                    return r;
                }
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return ok;
            },
        };

        var result = await Build(handler).ProxyImage("https://m.media-amazon.com/images/poster.jpg", null);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Contains(handler.Requested, u => u.Contains("covers.openlibrary.org"));
    }

    // T6.5/I-8 — the INITIAL url must pass the scheme guard, not just redirect hops.
    // An allowlisted HOST with a non-http(s) scheme must be rejected before any
    // network activity; HttpClient's own scheme rejection is not the security boundary.
    [Theory]
    [InlineData("ftp://m.media-amazon.com/images/poster.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://covers.openlibrary.org/1")]
    public async Task Proxy_NonHttpSchemeInitialUrl_IsRejected_AndNeverRequested(string url)
    {
        var handler = new ScriptedHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK),
        };

        var result = await Build(handler).ProxyImage(url, null);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(handler.Requested);
    }
}
