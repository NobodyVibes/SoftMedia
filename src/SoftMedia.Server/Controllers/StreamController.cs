using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;

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
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        IMediaService mediaService,
        ILogger<StreamController> logger)
    {
        _mediaService = mediaService;
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
