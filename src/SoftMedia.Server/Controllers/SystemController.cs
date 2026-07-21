using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Infrastructure;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoftMedia.Server.Controllers;

public record BrandingResponse(string ServerName, string? LoginMessage);
public record ConnectionInfoResponse(
    string MachineName,
    List<string> LanAddresses,
    string RequestScheme,
    string RequestHost,
    string? PublishedBaseUrl,
    bool ApiDocsEnabled);
public record LogsResponse(IReadOnlyList<LogEntry> Entries, string CurrentLevel);

/// <summary>
/// NR-WI-010/011 — server identity and operational introspection. Branding is the one
/// anonymous endpoint (the login page needs the server name before any credential
/// exists); everything else is admin-only.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/v1/system")]
public class SystemController : ControllerBase
{
    private readonly ISettingsService _settings;
    private readonly LogRingBuffer _logBuffer;
    private readonly IRuntimeLogLevel _runtimeLogLevel;

    public SystemController(ISettingsService settings, LogRingBuffer logBuffer, IRuntimeLogLevel runtimeLogLevel)
    {
        _settings = settings;
        _logBuffer = logBuffer;
        _runtimeLogLevel = runtimeLogLevel;
    }

    /// <summary>
    /// Server name + optional login message for the SPA's login screen. Anonymous by
    /// necessity and by design: the name is deliberate branding, and nothing else
    /// (versions, paths, users) is disclosed.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("branding")]
    public async Task<ActionResult<BrandingResponse>> Branding()
    {
        var name = await _settings.GetSettingAsync("ServerName", "SoftMedia");
        var message = await _settings.GetSettingAsync("LoginMessage", "");
        return Ok(new BrandingResponse(
            string.IsNullOrWhiteSpace(name) ? "SoftMedia" : name,
            string.IsNullOrWhiteSpace(message) ? null : message));
    }

    /// <summary>
    /// Read-only connection overview for the Server &amp; Network settings page. Ports and
    /// HTTPS remain infra-level (Kestrel/reverse proxy) — this card tells the operator
    /// what the server currently looks like from the network, not pretend to control it.
    /// </summary>
    [HttpGet("connection-info")]
    public async Task<ActionResult<ConnectionInfoResponse>> ConnectionInfo()
    {
        var lan = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                  && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var addr in nic.GetIPProperties().UnicastAddresses
                             .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    lan.Add(addr.Address.ToString());
                }
            }
        }
        catch
        {
            // NIC enumeration can fail in exotic containers/VMs — the card just shows
            // fewer rows; the request host below is always available.
        }

        var published = await _settings.GetSettingAsync("PublishedBaseUrl", "");
        var apiDocs = await _settings.GetSettingAsync("EnableApiDocs", "true");

        return Ok(new ConnectionInfoResponse(
            Environment.MachineName,
            lan,
            Request.Scheme,
            Request.Host.ToString(),
            string.IsNullOrWhiteSpace(published) ? null : published,
            string.Equals(apiDocs, "true", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Most recent server log entries from the in-memory ring buffer (NR-WI-011).
    /// Read-only; nothing on disk is exposed and the buffer is capped server-side.
    /// </summary>
    [HttpGet("logs")]
    public ActionResult<LogsResponse> Logs([FromQuery] int take = 200, [FromQuery] string? minLevel = null)
    {
        return Ok(new LogsResponse(_logBuffer.Snapshot(take, minLevel), _runtimeLogLevel.Current));
    }
}
