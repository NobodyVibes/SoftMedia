using System.Net;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

public class NetworkClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]      // loopback
    [InlineData("::1", true)]            // IPv6 loopback
    [InlineData("10.0.0.5", true)]       // 10.0.0.0/8
    [InlineData("172.16.4.9", true)]     // 172.16.0.0/12 low
    [InlineData("172.31.255.1", true)]   // 172.16.0.0/12 high
    [InlineData("192.168.1.10", true)]   // 192.168.0.0/16
    [InlineData("169.254.1.1", true)]    // link-local
    [InlineData("fd12:3456::1", true)]   // IPv6 unique-local
    [InlineData("8.8.8.8", false)]       // public IPv4
    [InlineData("172.32.0.1", false)]    // just outside 172.16/12
    [InlineData("172.15.0.1", false)]    // just below 172.16/12
    [InlineData("2606:4700::1111", false)] // public IPv6
    public void IsLan_ClassifiesCorrectly(string ip, bool expected)
    {
        Assert.Equal(expected, NetworkClassifier.IsLan(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsLan_NullIp_IsWan()
    {
        Assert.False(NetworkClassifier.IsLan((IPAddress?)null));
        Assert.False(NetworkClassifier.IsLan((string?)null));
    }

    [Fact]
    public void IsLan_Ipv4MappedToIpv6_Unwraps()
    {
        // ::ffff:192.168.1.5 should be treated as the LAN IPv4 it wraps.
        Assert.True(NetworkClassifier.IsLan(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.False(NetworkClassifier.IsLan(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void IsLan_GarbageString_IsWan()
    {
        Assert.False(NetworkClassifier.IsLan("not-an-ip"));
    }
}
