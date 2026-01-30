using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for serving media streams with HTTP Range Request support.
/// </summary>
[Authorize]
[ApiController]
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
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}
