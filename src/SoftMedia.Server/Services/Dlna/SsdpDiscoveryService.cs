using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Dlna;

/// <summary>
/// SSDP (UPnP discovery) for the DLNA Media Server: answers M-SEARCH probes and periodically
/// multicasts ssdp:alive so TVs find the server. Runs ONLY when EnableDlna is set (read at
/// startup — toggling requires a restart). Entirely best-effort and wrapped in try/catch: a
/// discovery failure must never affect the rest of the server, and the HTTP /dlna endpoints
/// work regardless.
///
/// NOTE: this is the one DLNA piece that cannot be verified without a real LAN + TV. It needs
/// on-device testing (see docs/user-docs/features/dlna.md).
/// </summary>
public class SsdpDiscoveryService : BackgroundService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;

    private readonly DlnaServerInfo _info;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServer _server;
    private readonly ILogger<SsdpDiscoveryService> _logger;

    public SsdpDiscoveryService(DlnaServerInfo info, IServiceScopeFactory scopeFactory, IServer server, ILogger<SsdpDiscoveryService> logger)
    {
        _info = info;
        _scopeFactory = scopeFactory;
        _server = server;
        _logger = logger;
    }

    private string Usn(string suffix) => suffix.Length == 0 ? $"uuid:{_info.Udn}" : $"uuid:{_info.Udn}::{suffix}";

    /// (NT/ST value, USN suffix) pairs this MediaServer advertises.
    private static readonly (string Nt, string Suffix)[] AdvertisedTypes =
    {
        ("upnp:rootdevice", "upnp:rootdevice"),
        ("", ""), // the bare uuid
        ("urn:schemas-upnp-org:device:MediaServer:1", "urn:schemas-upnp-org:device:MediaServer:1"),
        ("urn:schemas-upnp-org:service:ContentDirectory:1", "urn:schemas-upnp-org:service:ContentDirectory:1"),
        ("urn:schemas-upnp-org:service:ConnectionManager:1", "urn:schemas-upnp-org:service:ConnectionManager:1"),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool enabled;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            enabled = await settings.GetSettingAsync("EnableDlna", false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLNA: could not read EnableDlna; SSDP disabled.");
            return;
        }
        if (!enabled) return;

        var location = ResolveLocationUrl();
        if (location == null)
        {
            _logger.LogWarning("DLNA: could not resolve a LAN address/port; SSDP disabled.");
            return;
        }

        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            udp.JoinMulticastGroup(MulticastAddress);
            _logger.LogInformation("DLNA SSDP started; advertising {Location}", location);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLNA: SSDP socket bind failed (port 1900 in use?). Discovery disabled; HTTP endpoints still work.");
            udp?.Dispose();
            return;
        }

        await SafeNotifyAsync(udp, location, alive: true); // initial announce burst
        var lastAlive = DateTime.UtcNow;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "DLNA SSDP receive error"); continue; }

                HandleDatagram(udp, result, location);

                // Re-announce roughly every 15 minutes (max-age is 1800s).
                if (DateTime.UtcNow - lastAlive > TimeSpan.FromMinutes(15))
                {
                    await SafeNotifyAsync(udp, location, alive: true);
                    lastAlive = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            await SafeNotifyAsync(udp, location, alive: false); // byebye
            try { udp.DropMulticastGroup(MulticastAddress); } catch { }
            udp.Dispose();
        }
    }

    private void HandleDatagram(UdpClient udp, UdpReceiveResult result, string location)
    {
        try
        {
            var text = Encoding.ASCII.GetString(result.Buffer);
            if (!text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) return;

            var st = HeaderValue(text, "ST")?.Trim() ?? "ssdp:all";
            foreach (var (nt, suffix) in AdvertisedTypes)
            {
                var typeId = suffix.Length == 0 ? $"uuid:{_info.Udn}" : nt;
                if (st == "ssdp:all" || st == typeId || (st == "upnp:rootdevice" && nt == "upnp:rootdevice"))
                {
                    var reply = BuildSearchResponse(location, st == "ssdp:all" ? nt : st, suffix);
                    var bytes = Encoding.ASCII.GetBytes(reply);
                    udp.Send(bytes, bytes.Length, result.RemoteEndPoint);
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "DLNA SSDP handle error"); }
    }

    private string BuildSearchResponse(string location, string st, string suffix) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        $"DATE: {DateTime.UtcNow:r}\r\n" +
        "EXT:\r\n" +
        $"LOCATION: {location}\r\n" +
        "SERVER: SoftMedia/1.0 UPnP/1.0 DLNADOC/1.50\r\n" +
        $"ST: {(st.Length == 0 ? "upnp:rootdevice" : st)}\r\n" +
        $"USN: {Usn(suffix)}\r\n\r\n";

    private async Task SafeNotifyAsync(UdpClient udp, string location, bool alive)
    {
        try
        {
            var endpoint = new IPEndPoint(MulticastAddress, SsdpPort);
            foreach (var (nt, suffix) in AdvertisedTypes)
            {
                var msg =
                    "NOTIFY * HTTP/1.1\r\n" +
                    $"HOST: 239.255.255.250:{SsdpPort}\r\n" +
                    "CACHE-CONTROL: max-age=1800\r\n" +
                    $"LOCATION: {location}\r\n" +
                    "SERVER: SoftMedia/1.0 UPnP/1.0 DLNADOC/1.50\r\n" +
                    $"NT: {(nt.Length == 0 ? $"uuid:{_info.Udn}" : nt)}\r\n" +
                    $"NTS: ssdp:{(alive ? "alive" : "byebye")}\r\n" +
                    $"USN: {Usn(suffix)}\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(msg);
                await udp.SendAsync(bytes, bytes.Length, endpoint);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "DLNA SSDP notify error"); }
    }

    /// Builds http://{lan-ip}:{port}/dlna/description.xml from the server's bound port + a LAN NIC.
    private string? ResolveLocationUrl()
    {
        int? port = null;
        try
        {
            var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
            foreach (var addr in addresses ?? Enumerable.Empty<string>())
            {
                if (Uri.TryCreate(addr.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri) && uri.Scheme == "http")
                {
                    port = uri.Port;
                    break;
                }
            }
        }
        catch { /* fall through */ }
        port ??= 5011;

        var ip = PrimaryLanIPv4();
        return ip == null ? null : $"http://{ip}:{port}/dlna/description.xml";
    }

    private static IPAddress? PrimaryLanIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && NetworkClassifier.IsLan(a) && !IPAddress.IsLoopback(a));
        }
        catch { return null; }
    }

    private static string? HeaderValue(string message, string header)
    {
        foreach (var line in message.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && line[..idx].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return null;
    }
}
