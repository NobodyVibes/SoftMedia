using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// SSRF guard: ImageCacheService allowlists the request host, but must NOT let an
/// allowlisted host redirect the download to a non-allowlisted / internal address.
/// Redirects are followed manually with the allowlist re-applied on every hop.
public class ImageCacheServiceSsrfTests : IDisposable
{
    private readonly string _webRoot;

    public ImageCacheServiceSsrfTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "sm-ssrf-" + Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(_webRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Directory.GetParent(_webRoot)!.FullName, true); } catch { }
    }

    // Records every requested URL and returns scripted responses. A custom handler does
    // not implement auto-redirect, so the service's manual redirect logic is exercised.
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

    private ImageCacheService Build(ScriptedHandler handler)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);
        return new ImageCacheService(new HttpClient(handler), NullLogger<ImageCacheService>.Instance, env.Object);
    }

    [Fact]
    public async Task Redirect_ToInternalAddress_IsBlocked_AndNeverRequested()
    {
        const string internalTarget = "http://169.254.169.254/latest/meta-data/";
        var handler = new ScriptedHandler
        {
            Respond = uri =>
            {
                if (uri.Host == "m.media-amazon.com")
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                    r.Headers.Location = new Uri(internalTarget);
                    return r;
                }
                // If the guard were broken and this were fetched, it would "succeed".
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return ok;
            },
        };

        var result = await Build(handler).CacheMoviePosterAsync(
            Guid.NewGuid(), "https://m.media-amazon.com/images/poster.jpg");

        // Falls back to the original URL (download failed) and the internal host was
        // never contacted.
        Assert.Equal("https://m.media-amazon.com/images/poster.jpg", result);
        Assert.DoesNotContain(handler.Requested, u => u.Contains("169.254.169.254"));
    }

    [Fact]
    public async Task Redirect_ToAllowlistedHost_IsFollowed_AndCached()
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

        var id = Guid.NewGuid();
        var result = await Build(handler).CacheMoviePosterAsync(id, "https://m.media-amazon.com/images/poster.jpg");

        // The redirect to an allowlisted host is followed and the image is cached.
        Assert.StartsWith("/cache/images/movies/", result);
        Assert.Contains(handler.Requested, u => u.Contains("covers.openlibrary.org"));
    }

    [Fact]
    public async Task Redirect_ToInternetArchiveDatanode_IsFollowed_AndCached()
    {
        // Cover Art Archive "front" URLs 307-redirect through archive.org to a
        // per-release Internet Archive storage node (iaNNN.us.archive.org), a
        // subdomain of archive.org but NOT an exact allowlist member. The host
        // suffix match must follow it so album covers actually download.
        var handler = new ScriptedHandler
        {
            Respond = uri =>
            {
                if (uri.Host == "coverartarchive.org")
                {
                    var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    r.Headers.Location = new Uri("https://ia800504.us.archive.org/cover.jpg");
                    return r;
                }
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return ok;
            },
        };

        var result = await Build(handler).CacheAlbumCoverAsync(
            Guid.NewGuid(), "https://coverartarchive.org/release-group/abc/front");

        Assert.StartsWith("/cache/images/music/", result);
        Assert.Contains(handler.Requested, u => u.Contains("ia800504.us.archive.org"));
    }

    [Fact]
    public async Task Redirect_ToArchiveOrgLookalike_IsBlocked()
    {
        // A look-alike host that merely ends with "archive.org" but has no dot
        // boundary ("evilarchive.org") must NOT be treated as a subdomain of
        // archive.org — the suffix is anchored on ".archive.org".
        const string url = "https://coverartarchive.org/release-group/abc/front";
        var handler = new ScriptedHandler
        {
            Respond = uri =>
            {
                if (uri.Host == "coverartarchive.org")
                {
                    var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    r.Headers.Location = new Uri("https://evilarchive.org/cover.jpg");
                    return r;
                }
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                ok.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return ok;
            },
        };

        var result = await Build(handler).CacheAlbumCoverAsync(Guid.NewGuid(), url);

        // Download failed (redirect blocked); falls back to the original URL and
        // the look-alike host was never contacted.
        Assert.Equal(url, result);
        Assert.DoesNotContain(handler.Requested, u => u.Contains("evilarchive.org"));
    }
}
