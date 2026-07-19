using System.Net;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Classifies a playback client into a coarse form factor for the admin Now-Playing
/// dashboard, from the User-Agent the client already sends (no client changes needed).
///
/// Deliberately coarse: the dashboard shows ONE icon per row, so the goal is "what kind
/// of thing is this" (a phone vs a TV), not browser/OS version reporting. Values are
/// wire constants consumed by the client's icon map — keep them in sync with
/// ActiveSessionsCard's deviceIcon().
///
/// SoftMedia currently ships only the browser app; when native apps arrive they can
/// announce themselves explicitly and this becomes the fallback.
/// </summary>
public static class ClientDeviceClassifier
{
    public const string Desktop = "Desktop";
    public const string Mobile = "Mobile";
    public const string Tablet = "Tablet";
    public const string Tv = "Tv";
    public const string Cast = "Cast";
    public const string Unknown = "Unknown";

    /// <summary>
    /// ORDER MATTERS. TV and Cast are tested first because streaming devices embed the
    /// mobile/desktop platform tokens they are built on — an Android TV and a Fire TV
    /// both say "Android", and every Chromecast UA also says "Linux". Tablet precedes
    /// Mobile for the same reason (an Android tablet is "Android" WITHOUT "Mobile").
    /// </summary>
    public static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return Unknown;
        var ua = userAgent.ToLowerInvariant();

        // Cast receivers — CrKey is the Chromecast token.
        if (ua.Contains("crkey") || ua.Contains("chromecast")) return Cast;

        // TV platforms + consoles (which are used as TV clients).
        if (ua.Contains("smarttv") || ua.Contains("smart-tv") || ua.Contains("googletv")
            || ua.Contains("appletv") || ua.Contains("tizen") || ua.Contains("web0s")
            || ua.Contains("webos") || ua.Contains("netcast") || ua.Contains("hbbtv")
            || ua.Contains("bravia") || ua.Contains("roku") || ua.Contains("aft")   // Fire TV: AFTB/AFTS/AFTM…
            || ua.Contains("playstation") || ua.Contains("xbox")
            || ua.Contains("android tv") || ua.Contains("tv safari"))
            return Tv;

        // Explicit PHONE tokens outrank the tablet heuristic below, which is only an
        // inference ("Android without Mobile") and would otherwise capture a device that
        // already said outright that it is a phone.
        if (ua.Contains("iphone") || ua.Contains("ipod") || ua.Contains("windows phone")
            || ua.Contains("iemobile"))
            return Mobile;

        // Tablets — an Android tablet omits the "mobile" token that phones carry.
        if (ua.Contains("ipad") || ua.Contains("tablet") || ua.Contains("kindle") || ua.Contains("silk")
            || (ua.Contains("android") && !ua.Contains("mobile")))
            return Tablet;

        // Remaining phones (Android phones, and anything self-declaring "mobile").
        if (ua.Contains("android") || ua.Contains("mobile"))
            return Mobile;

        // Desktop browsers.
        if (ua.Contains("windows nt") || ua.Contains("macintosh") || ua.Contains("cros")
            || ua.Contains("x11") || ua.Contains("linux"))
            return Desktop;

        return Unknown;
    }

    /// <summary>
    /// Display form of the client address. IPv4-mapped IPv6 (<c>::ffff:192.168.1.5</c> — what
    /// Kestrel reports for an IPv4 client on a dual-stack socket) is unwrapped to plain IPv4 so
    /// the dashboard shows the address an admin would recognise from their router.
    /// Behind a reverse proxy this is already the real client address (the forwarded-headers
    /// middleware rewrites RemoteIpAddress for trusted proxies).
    /// </summary>
    public static string? NormalizeIp(IPAddress? ip)
    {
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
