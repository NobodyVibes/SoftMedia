using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Sessions;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for serving media streams with HTTP Range Request support.
/// Authentication flows through the standard JwtBearer middleware: requests
/// carrying a Bearer header, or a `?token=` / `?access_token=` query parameter
/// (lifted by <c>JwtBearerEvents.OnMessageReceived</c> in ServiceCollectionExtensions),
/// satisfy the class-level <see cref="AuthorizeAttribute"/>.
/// </summary>
[ApiController]
// B-18: media CONTENT requires the read:library scope for API tokens — gating
// search while leaving /stream open would be backwards. Media/cast query tokens
// authenticate under JwtBearer without scope claims, so they are unaffected.
[Authorize(Policy = ScopePolicies.ReadLibrary)]
[Route("api/v1/[controller]")]
public class StreamController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IActiveStreamRegistry _streamRegistry;
    private readonly AppDbContext _context;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        IMediaService mediaService,
        IActiveStreamRegistry streamRegistry,
        AppDbContext context,
        ILogger<StreamController> logger)
    {
        _mediaService = mediaService;
        _streamRegistry = streamRegistry;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Streams the media file with HTTP Range Request support for seeking.
    /// Supports both GET (stream content) and HEAD (probe headers) for vidstack compatibility.
    /// </summary>
    [HttpGet("{id}")]
    [HttpHead("{id}")]
    public async Task<IActionResult> GetStream(Guid id)
    {
        try
        {
            var streamInfo = await _mediaService.GetStreamInfoAsync(id);
            if (streamInfo == null)
            {
                return NotFound();
            }

            // B-01: direct play serves the ORIGINAL file — the last uncapped path.
            // A bitrate-capped user's VIDEO must transcode (where `-maxrate` applies);
            // the plan endpoint already refuses direct play over the cap, and this
            // gate closes the "hit /stream directly, ignore the plan" bypass.
            // Music is exempt: the cap is a video-streaming control, and audio
            // bitrates would otherwise make small caps silently kill all music.
            if (streamInfo.Type is Models.MediaType.Movie or Models.MediaType.Episode
                && streamInfo.Bitrate is > 0
                && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var capUserId))
            {
                var capKbps = await _context.Users
                    .Where(u => u.Id == capUserId)
                    .Select(u => u.MaxStreamBitrateKbps)
                    .FirstOrDefaultAsync();
                if (capKbps is > 0 && streamInfo.Bitrate.Value / 1000 > capKbps.Value)
                {
                    _logger.LogWarning(
                        "Direct play refused for {MediaId}: source {SourceKbps} kbps exceeds user cap {CapKbps} kbps",
                        id, streamInfo.Bitrate.Value / 1000, capKbps.Value);
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { error = "This item exceeds your streaming bitrate limit — use the transcoded stream." });
                }
            }

            // R-WI-016: register direct-play liveness by RESPONSE LIFETIME (a range
            // response can be one multi-hour request — per-request counting is wrong).
            // O(1) dictionary stamp either side of the file result; HEAD probes are
            // metadata-only and never represent playback.
            if (!HttpMethods.IsHead(Request.Method)
                && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var streamUserId))
            {
                // The entry HANDLE is captured so the completion callback releases the
                // exact generation it incremented (key-based release could hit a
                // recreated entry after a prune race).
                var entry = _streamRegistry.OnResponseStarted(streamUserId, id);
                Response.OnCompleted(() =>
                {
                    _streamRegistry.OnResponseEnded(entry);
                    return Task.CompletedTask;
                });
            }

            // Serve the file with Range processing enabled (HTTP 206 Partial Content)
            return PhysicalFile(streamInfo.Path, streamInfo.ContentType, enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Return 404 (not 403) so unauthorized callers cannot probe whether
            // a particular media ID exists on the server. The MediaService throws
            // this when the stored path escaped its library jail — the user is
            // authenticated but the resource is effectively "not there" for them.
            _logger.LogWarning(ex, "Stream access blocked by library jail for id {Id}", id);
            return NotFound();
        }
    }
}
