using System.Net;
using Microsoft.AspNetCore.Hosting;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Server-hosted SPA (2026-08-02, NR-WI-015 prerequisite): when the built client is
/// deployed into wwwroot, unmatched non-API paths fall back to index.html (deep links
/// load the app, and the security headers — incl. the CSP — finally reach the
/// document). API/hub/cache/swagger prefixes keep their plain 404 (anti-probe + the
/// client's error handling both rely on JSON-shaped API errors, never an HTML shell),
/// and with no index.html deployed the behavior is exactly the pre-SPA 404.
public class SpaHostingTests : IClassFixture<SoftMediaWebApplicationFactory>
{
    private const string Sentinel = "<!-- spa-hosting-test-sentinel -->";

    private readonly SoftMediaWebApplicationFactory _factory;

    public SpaHostingTests(SoftMediaWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/media/00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData("/settings/client/playback")]
    public async Task ClientRoutes_FallBackToIndexHtml(string path)
    {
        using var index = await TempIndexAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains(Sentinel, await response.Content.ReadAsStringAsync());
        // The shell must revalidate every load; only the hashed /assets are long-lived.
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        // Single-origin hosting is the point: the CSP must ride on the document.
        Assert.True(
            response.Headers.Contains("Content-Security-Policy")
            || response.Headers.Contains("Content-Security-Policy-Report-Only"),
            "SPA document is missing the CSP header");
    }

    [Theory]
    [InlineData("/api/v1/definitely-not-a-route")]
    [InlineData("/hubs/definitely-not-a-hub")]
    [InlineData("/cache/definitely-not-cached/x.bin")]
    [InlineData("/swagger/definitely-not-docs.json")]
    public async Task ApiShapedPaths_Stay404_NeverTheHtmlShell(string path)
    {
        using var index = await TempIndexAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutADeployedClient_UnmatchedPaths_Stay404()
    {
        // No index.html in wwwroot (the default checkout state) → pre-SPA behavior.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/media/00112233-4455-6677-8899-aabbccddeeff");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Csp_IsEnforcingByDefault_AndReportOnlyWhenOptedOut()
    {
        // The shipped default flipped to ENFORCING on 2026-08-02 (post-audit);
        // Security:EnforceCsp=false is the operator's documented rollback lever.
        var defaultClient = _factory.CreateClient();
        var defaultResponse = await defaultClient.GetAsync("/api/v1/health");
        Assert.True(defaultResponse.Headers.Contains("Content-Security-Policy"));
        Assert.False(defaultResponse.Headers.Contains("Content-Security-Policy-Report-Only"));

        using var reportOnlyFactory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Security:EnforceCsp", "false"));
        var reportOnlyClient = reportOnlyFactory.CreateClient();
        var reportOnlyResponse = await reportOnlyClient.GetAsync("/api/v1/health");
        Assert.True(reportOnlyResponse.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.False(reportOnlyResponse.Headers.Contains("Content-Security-Policy"));
    }

    // ---- helpers -------------------------------------------------------------

    /// A throwaway index.html in the REAL wwwroot (the factory's content root is the
    /// server project — same pattern as CacheStaticServingTests.TempImageAsync).
    /// Deleted on dispose, even when an assertion fails.
    private static async Task<TempIndex> TempIndexAsync()
    {
        var webRoot = FindWebRootUpwards();
        var path = Path.Combine(webRoot, "index.html");
        await File.WriteAllTextAsync(path, $"<!doctype html><html><body>{Sentinel}</body></html>");
        return new TempIndex(path);
    }

    private sealed record TempIndex(string FilePath) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(FilePath); } catch { }
        }
    }

    private static string FindWebRootUpwards()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "SoftMedia.Server", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "src", "SoftMedia.Server", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate SoftMedia.Server/wwwroot above the test assembly.");
    }
}
