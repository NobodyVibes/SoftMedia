using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;
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
[Authorize]
[Route("api/v1/[controller]")]
public class StreamController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IActiveStreamRegistry _streamRegistry;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        IMediaService mediaService,
        IActiveStreamRegistry streamRegistry,
        ILogger<StreamController> logger)
    {
        _mediaService = mediaService;
        _streamRegistry = streamRegistry;
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
