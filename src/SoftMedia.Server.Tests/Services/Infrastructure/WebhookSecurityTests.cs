using System.Net;
using System.Security.Cryptography;
using System.Text;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Unit tests for the pure webhook signing + SSRF-validation helpers (P2-WI-004).
public class WebhookSecurityTests
{
    [Fact]
    public void Sign_ProducesVerifiableHmacSha256()
    {
        const string secret = "topsecret";
        const string body = "{\"event\":\"webhook.test\"}";

        var sig = WebhookSecurity.Sign(secret, body);

        Assert.StartsWith("sha256=", sig);
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(expected, sig);
    }

    private static Uri U(string s) => new(s);
    private static IReadOnlyList<IPAddress> Ips(params string[] ips) => ips.Select(IPAddress.Parse).ToList();

    [Fact]
    public void ValidateTarget_AllowsPublicHttps()
    {
        Assert.Null(WebhookSecurity.ValidateTarget(U("https://example.com/hook"), Ips("93.184.216.34"), allowHttp: false, allowLoopback: false));
    }

    [Fact]
    public void ValidateTarget_RejectsNonHttpScheme()
    {
        Assert.NotNull(WebhookSecurity.ValidateTarget(U("ftp://example.com"), Ips("93.184.216.34"), false, false));
    }

    [Fact]
    public void ValidateTarget_RejectsLoopback_WhenDisallowed()
    {
        var reason = WebhookSecurity.ValidateTarget(U("https://localhost/hook"), Ips("127.0.0.1"), allowHttp: false, allowLoopback: false);
        Assert.Contains("Loopback", reason);
    }

    [Fact]
    public void ValidateTarget_AllowsLoopback_WhenEnabled()
    {
        Assert.Null(WebhookSecurity.ValidateTarget(U("https://localhost/hook"), Ips("127.0.0.1"), allowHttp: false, allowLoopback: true));
    }

    [Fact]
    public void ValidateTarget_RejectsPublicHttp_ByDefault()
    {
        var reason = WebhookSecurity.ValidateTarget(U("http://example.com/hook"), Ips("93.184.216.34"), allowHttp: false, allowLoopback: false);
        Assert.Contains("Plain-HTTP", reason);
    }

    [Fact]
    public void ValidateTarget_AllowsPrivateHttp_ByDefault()
    {
        // HTTP to a private RFC1918 target is fine without allowHttp (it's not public).
        Assert.Null(WebhookSecurity.ValidateTarget(U("http://192.168.1.50/hook"), Ips("192.168.1.50"), allowHttp: false, allowLoopback: false));
    }

    [Fact]
    public void ValidateTarget_RejectsMixedPublicAndPrivateResolution()
    {
        // DNS-rebinding shape: host resolves to both public and private.
        var reason = WebhookSecurity.ValidateTarget(U("https://evil.example/hook"), Ips("93.184.216.34", "10.0.0.5"), false, false);
        Assert.Contains("both public and internal", reason);
    }
}
