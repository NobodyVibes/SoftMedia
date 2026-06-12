using System.Net;
using System.Net.Sockets;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Classifies a client IP as LAN (local) or WAN (remote) so streaming policy can
/// apply different bitrate ceilings. "LAN" = loopback, link-local, RFC 1918 private
/// IPv4, or IPv6 unique-local (fc00::/7). Everything else (including a null/unknown
/// IP) is treated as WAN — fail-safe toward the stricter remote cap.
///
/// Depends on the forwarded-headers resolution from P0-WI-001: behind a reverse
/// proxy the RemoteIpAddress is the real client only once UseForwardedHeaders has run.
/// </summary>
public static class NetworkClassifier
{
    public static bool IsLan(IPAddress? ip)
    {
        if (ip == null) return false;

        if (IPAddress.IsLoopback(ip)) return true;
        // Audit wave-2 L-11: the unspecified address (0.0.0.0 / :: / ::ffff:0.0.0.0) connects to
        // loopback on Linux, so it must be treated as internal (and is gated as loopback below) —
        // otherwise a hostile DNS record pointing at 0.0.0.0 was classified "public" and bypassed
        // the SSRF guard's AllowLoopback=false.
        if (IsUnspecified(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes(); // network order
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 link-local
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 CGNAT (RFC 6598) — used by Tailscale / ISP carrier-grade NAT; not
            // public, must not pass the SSRF guard as a routable target (audit wave-2 L-12).
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            // Unique-local fc00::/7 — first byte 0xFC or 0xFD.
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            // IPv4-mapped IPv6 (::ffff:a.b.c.d) — unwrap and re-test.
            if (ip.IsIPv4MappedToIPv6) return IsLan(ip.MapToIPv4());
            return false;
        }

        return false;
    }

    public static bool IsLan(string? ip)
        => !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) && IsLan(parsed);

    /// <summary>Loopback (127.0.0.0/8, ::1) OR the unspecified address (0.0.0.0 / ::), which
    /// connects to loopback on Linux. Used by the webhook SSRF guard to gate AllowLoopbackWebhooks
    /// independently of private ranges (P2-WI-004; unspecified added in audit wave-2 L-11).</summary>
    public static bool IsLoopback(IPAddress? ip) => ip != null && (IPAddress.IsLoopback(ip) || IsUnspecified(ip));

    /// <summary>The unspecified / "any" address: 0.0.0.0, ::, or the IPv4-mapped ::ffff:0.0.0.0.</summary>
    private static bool IsUnspecified(IPAddress ip)
    {
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.IsIPv4MappedToIPv6 && ip.MapToIPv4().Equals(IPAddress.Any)) return true;
        return false;
    }

    /// <summary>RFC 1918 private IPv4 / link-local / IPv6 ULA — i.e. LAN but NOT loopback.
    /// Used to allow plain-HTTP webhooks to private targets while still blocking public HTTP.</summary>
    public static bool IsPrivate(IPAddress? ip) => ip != null && !IPAddress.IsLoopback(ip) && IsLan(ip);
}
