using System.Net;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Device classification for the admin Now-Playing dashboard. The ordering rules carry the
/// real risk: streaming devices embed the platform tokens they are built on (an Android TV
/// says "Android", a Chromecast says "Linux"), so a naive substring order mislabels them.
public class ClientDeviceClassifierTests
{
    [Theory]
    // Phones
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15", ClientDeviceClassifier.Mobile)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Mobile Safari/537.36", ClientDeviceClassifier.Mobile)]
    // A real Windows Phone UA carries BOTH "Windows Phone" and a bare "Android" token; the
    // explicit phone token must win over the "Android without Mobile" tablet inference.
    [InlineData("Mozilla/5.0 (Windows Phone 10.0; Android 6.0.1; Microsoft; Lumia 950) AppleWebKit/537.36 Edge/15", ClientDeviceClassifier.Mobile)]
    // Tablets — Android tablets omit the "Mobile" token that phones carry.
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_2 like Mac OS X) AppleWebKit/605.1.15", ClientDeviceClassifier.Tablet)]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 Safari/537.36", ClientDeviceClassifier.Tablet)]
    [InlineData("Mozilla/5.0 (Linux; U; Android 9; KFMAWI) Silk/119.1 like Chrome", ClientDeviceClassifier.Tablet)]
    // TVs and consoles — each of these ALSO matches a phone/desktop token, so they pin the order.
    [InlineData("Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36", ClientDeviceClassifier.Tv)]
    [InlineData("Mozilla/5.0 (Web0S; Linux/SmartTV) AppleWebKit/537.36", ClientDeviceClassifier.Tv)]
    [InlineData("Mozilla/5.0 (Linux; Android 12; AFTKA Build/STT1.240middle) Mobile Safari", ClientDeviceClassifier.Tv)]
    [InlineData("Mozilla/5.0 (Linux; Android 10; BRAVIA 4K) AppleWebKit/537.36", ClientDeviceClassifier.Tv)]
    [InlineData("Roku4640X/DVP-7.70 (297.70E04154A)", ClientDeviceClassifier.Tv)]
    [InlineData("Mozilla/5.0 (PlayStation; PlayStation 5/2.26) AppleWebKit/605.1.15", ClientDeviceClassifier.Tv)]
    // Cast receivers.
    [InlineData("Mozilla/5.0 (X11; Linux) AppleWebKit/537.36 CrKey/1.56.500000", ClientDeviceClassifier.Cast)]
    // Desktop browsers.
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", ClientDeviceClassifier.Desktop)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15", ClientDeviceClassifier.Desktop)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/120", ClientDeviceClassifier.Desktop)]
    public void Classifies_representative_user_agents(string userAgent, string expected)
    {
        Assert.Equal(expected, ClientDeviceClassifier.Classify(userAgent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.4.0")] // a non-browser client carries no platform token
    public void Unrecognised_or_missing_agents_are_Unknown(string? userAgent)
    {
        Assert.Equal(ClientDeviceClassifier.Unknown, ClientDeviceClassifier.Classify(userAgent));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_unwrapped_to_the_address_an_admin_would_recognise()
    {
        // What Kestrel reports for an IPv4 client on a dual-stack socket.
        Assert.Equal("192.168.1.50", ClientDeviceClassifier.NormalizeIp(IPAddress.Parse("::ffff:192.168.1.50")));
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::1", "::1")]
    public void Plain_addresses_pass_through(string input, string expected)
    {
        Assert.Equal(expected, ClientDeviceClassifier.NormalizeIp(IPAddress.Parse(input)));
    }

    [Fact]
    public void Null_address_stays_null() => Assert.Null(ClientDeviceClassifier.NormalizeIp(null));
}
