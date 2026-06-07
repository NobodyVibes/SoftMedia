using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Dlna;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// DLNA / UPnP Media Server (P4-004). Lets a smart-TV media player (e.g. LG webOS) discover and
/// play the library directly over the LAN — no Chromecast, no certificate.
///
/// SECURITY: DLNA has no authentication (TVs can't log in), so this surface is **unauthenticated**.
/// It is therefore gated three ways: (1) opt-in via the EnableDlna setting (default off), (2)
/// LAN-only — non-LAN client IPs get 404, and (3) path-jail validation on the served file. When
/// enabled it exposes the whole audio/video library to anyone on the LAN, by design.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("dlna")]
public class DlnaController : ControllerBase
{
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string CdNs = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string CmNs = "urn:schemas-upnp-org:service:ConnectionManager:1";

    private readonly ISettingsService _settings;
    private readonly IDlnaContentDirectory _contentDirectory;
    private readonly DlnaServerInfo _info;
    private readonly AppDbContext _db;
    private readonly IStreamSecurityService _security;
    private readonly ILogger<DlnaController> _logger;

    public DlnaController(
        ISettingsService settings,
        IDlnaContentDirectory contentDirectory,
        DlnaServerInfo info,
        AppDbContext db,
        IStreamSecurityService security,
        ILogger<DlnaController> logger)
    {
        _settings = settings;
        _contentDirectory = contentDirectory;
        _info = info;
        _db = db;
        _security = security;
        _logger = logger;
    }

    /// Enabled (opt-in) AND the caller is on the LAN. Everything else 404s (anti-probe).
    private async Task<bool> AllowedAsync()
        => await _settings.GetSettingAsync("EnableDlna", false)
           && NetworkClassifier.IsLan(HttpContext.Connection.RemoteIpAddress);

    private static IActionResult Xml(string content) => new ContentResult { Content = content, ContentType = "text/xml; charset=\"utf-8\"", StatusCode = 200 };

    [HttpGet("description.xml")]
    public async Task<IActionResult> Description()
    {
        if (!await AllowedAsync()) return NotFound();
        var name = await _settings.GetSettingAsync("DlnaServerName", "SoftMedia");
        return Xml(DlnaDescriptions.DeviceDescription(name, _info.Udn));
    }

    [HttpGet("cd/scpd.xml")]
    public async Task<IActionResult> ContentDirectoryScpd()
        => !await AllowedAsync() ? NotFound() : Xml(DlnaDescriptions.ContentDirectoryScpd);

    [HttpGet("cm/scpd.xml")]
    public async Task<IActionResult> ConnectionManagerScpd()
        => !await AllowedAsync() ? NotFound() : Xml(DlnaDescriptions.ConnectionManagerScpd);

    [HttpPost("cd/control")]
    public async Task<IActionResult> ContentDirectoryControl()
    {
        if (!await AllowedAsync()) return NotFound();

        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        var action = SoapActionLocalName(Request.Headers["SOAPACTION"].ToString(), body);
        if (action != "Browse")
            // GetSearchCapabilities / GetSortCapabilities / GetSystemUpdateID — return empty/0.
            return Xml(SimpleCdResponse(action));

        var req = XDocument.Parse(body);
        var objectId = Arg(req, "ObjectID") ?? "0";
        var browseFlag = Arg(req, "BrowseFlag") ?? "BrowseDirectChildren";
        int.TryParse(Arg(req, "StartingIndex"), out var start);
        int.TryParse(Arg(req, "RequestedCount"), out var count);

        var result = await _contentDirectory.BrowseAsync(
            objectId, browseFlag == "BrowseMetadata", start, count, BaseUrl(), HttpContext.RequestAborted);

        var resp = $"""
<u:BrowseResponse xmlns:u="{CdNs}">
<Result>{System.Security.SecurityElement.Escape(result.Didl)}</Result>
<NumberReturned>{result.NumberReturned}</NumberReturned>
<TotalMatches>{result.TotalMatches}</TotalMatches>
<UpdateID>1</UpdateID>
</u:BrowseResponse>
""";
        return Xml(SoapEnvelope(resp));
    }

    [HttpPost("cm/control")]
    public async Task<IActionResult> ConnectionManagerControl()
    {
        if (!await AllowedAsync()) return NotFound();
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        var action = SoapActionLocalName(Request.Headers["SOAPACTION"].ToString(), body);

        var resp = action == "GetProtocolInfo"
            ? $"<u:GetProtocolInfoResponse xmlns:u=\"{CmNs}\"><Source>{System.Security.SecurityElement.Escape(DlnaProtocol.SourceProtocolInfo)}</Source><Sink></Sink></u:GetProtocolInfoResponse>"
            : $"<u:GetCurrentConnectionIDsResponse xmlns:u=\"{CmNs}\"><ConnectionIDs>0</ConnectionIDs></u:GetCurrentConnectionIDsResponse>";
        return Xml(SoapEnvelope(resp));
    }

    /// Minimal GENA: some TVs SUBSCRIBE before browsing. We send no events, but a 200 with a SID
    /// keeps them happy. Mapped for both services via the catch-all event route.
    [AcceptVerbs("SUBSCRIBE", "UNSUBSCRIBE")]
    [Route("cd/event")]
    [Route("cm/event")]
    public async Task<IActionResult> Event()
    {
        if (!await AllowedAsync()) return NotFound();
        Response.Headers["SID"] = $"uuid:{Guid.NewGuid()}";
        Response.Headers["TIMEOUT"] = "Second-1800";
        return Ok();
    }

    [HttpGet("media/{id:guid}")]
    [HttpHead("media/{id:guid}")]
    public async Task<IActionResult> Media(Guid id)
    {
        if (!await AllowedAsync()) return NotFound();

        var item = await _db.MediaItems.Include(m => m.Library).FirstOrDefaultAsync(m => m.Id == id, HttpContext.RequestAborted);
        if (item?.Library == null || !System.IO.File.Exists(item.Path)) return NotFound();
        // Defense in depth: the file must live inside one of the library's configured roots.
        if (!_security.IsPathAuthorized(item.Path, item.Library.Paths))
        {
            _logger.LogWarning("DLNA path-jail blocked {Path}", item.Path);
            return NotFound();
        }

        Response.Headers["transferMode.dlna.org"] = "Streaming";
        Response.Headers["contentFeatures.dlna.org"] = DlnaProtocol.VideoFlags;
        return PhysicalFile(item.Path, MimeTypeResolver.GetMimeType(item.Path), enableRangeProcessing: true);
    }

    // --- helpers -----------------------------------------------------------

    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}";

    private static string SoapEnvelope(string inner) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<s:Envelope xmlns:s=\"{SoapNs}\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>{inner}</s:Body></s:Envelope>";

    private static string SimpleCdResponse(string action) => SoapEnvelope(action switch
    {
        "GetSystemUpdateID" => $"<u:GetSystemUpdateIDResponse xmlns:u=\"{CdNs}\"><Id>1</Id></u:GetSystemUpdateIDResponse>",
        "GetSortCapabilities" => $"<u:GetSortCapabilitiesResponse xmlns:u=\"{CdNs}\"><SortCaps></SortCaps></u:GetSortCapabilitiesResponse>",
        _ => $"<u:GetSearchCapabilitiesResponse xmlns:u=\"{CdNs}\"><SearchCaps></SearchCaps></u:GetSearchCapabilitiesResponse>",
    });

    /// SOAPACTION header looks like "urn:...:ContentDirectory:1#Browse" — take the part after '#';
    /// fall back to the first child element name of the SOAP Body.
    private static string SoapActionLocalName(string soapAction, string body)
    {
        var hash = soapAction.LastIndexOf('#');
        if (hash >= 0) return soapAction[(hash + 1)..].Trim('"', ' ');
        try
        {
            var bodyEl = XDocument.Parse(body).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
            return bodyEl?.Elements().FirstOrDefault()?.Name.LocalName ?? "";
        }
        catch { return ""; }
    }

    private static string? Arg(XDocument doc, string name)
        => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}
