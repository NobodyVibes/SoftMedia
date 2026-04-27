using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// API endpoints for music-related resources (album covers, artist images).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/music")]
public class MusicController : ControllerBase
{
    private readonly IMusicImageService _imageService;
    private readonly IThumbnailService _thumbnailService;
    private readonly AppDbContext _context;
    private readonly ILogger<MusicController> _logger;

    public MusicController(
        IMusicImageService imageService,
        IThumbnailService thumbnailService,
        AppDbContext context,
        ILogger<MusicController> logger)
    {
        _imageService = imageService;
        _thumbnailService = thumbnailService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get album cover art.
    /// </summary>
    /// <param name="albumId">The album ID.</param>
    /// <returns>The cover image or 404.</returns>
    [HttpGet("album/{albumId}/cover")]
    // Browser `<img>` tags cannot set Authorization headers. Clients authenticate
    // these endpoints via a `?access_token=` query-string parameter, which the
    // JwtBearer `OnMessageReceived` handler in ServiceCollectionExtensions
    // lifts into `context.Token` for /api/v1/music and /api/v1/image paths.
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetAlbumCover(Guid albumId, [FromQuery] int? width)
    {
        return await ServeImageAsync(albumId, width, "Album cover");
    }

    /// <summary>
    /// Get artist image.
    /// </summary>
    /// <param name="artistId">The artist ID.</param>
    /// <returns>The artist image or 404.</returns>
    [HttpGet("artist/{artistId}/image")]
    // Browser `<img>` tags cannot set Authorization headers. Clients authenticate
    // these endpoints via a `?access_token=` query-string parameter, which the
    // JwtBearer `OnMessageReceived` handler in ServiceCollectionExtensions
    // lifts into `context.Token` for /api/v1/music and /api/v1/image paths.
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetArtistImage(Guid artistId, [FromQuery] int? width)
    {
        return await ServeImageAsync(artistId, width, "Artist image");
    }

    /// <summary>
    /// Get track cover art (resolves to album cover).
    /// </summary>
    /// <param name="trackId">The track ID.</param>
    /// <returns>The cover image or 404.</returns>
    [HttpGet("track/{trackId}/cover")]
    // Browser `<img>` tags cannot set Authorization headers. Clients authenticate
    // these endpoints via a `?access_token=` query-string parameter, which the
    // JwtBearer `OnMessageReceived` handler in ServiceCollectionExtensions
    // lifts into `context.Token` for /api/v1/music and /api/v1/image paths.
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetTrackCover(Guid trackId, [FromQuery] int? width)
    {
        return await ServeImageAsync(trackId, width, "Track cover");
    }

    private const int MinThumbnailWidth = 64;
    private const int MaxThumbnailWidth = 800;

    /// <summary>
    /// Resolve and stream an image file with ETag support.
    /// Uses PhysicalFile for zero-copy kernel-level streaming instead of buffering bytes in memory.
    /// Supports optional thumbnail generation via the width parameter.
    /// </summary>
    private async Task<IActionResult> ServeImageAsync(Guid mediaItemId, int? width, string debugLabel)
    {
        var info = await _imageService.GetImageInfoAsync(mediaItemId);
        if (info == null)
        {
            _logger.LogDebug("{Label} not found: {Id}", debugLabel, mediaItemId);
            return NotFound();
        }

        var servePath = info.Value.Path;
        var serveMime = info.Value.MimeType;

        // Generate thumbnail if width is requested and within allowed range
        if (width.HasValue && width.Value >= MinThumbnailWidth && width.Value <= MaxThumbnailWidth)
        {
            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(
                info.Value.Path, mediaItemId, width.Value);
            if (thumbPath != null)
            {
                servePath = thumbPath;
                serveMime = "image/webp";
            }
            // Fall through to full-size if thumbnail generation fails
        }

        var lastModified = System.IO.File.GetLastWriteTimeUtc(servePath);
        Response.Headers["ETag"] = $"\"{lastModified.Ticks}\"";
        Response.Headers["Last-Modified"] = lastModified.ToString("R");

        return PhysicalFile(servePath, serveMime, enableRangeProcessing: true);
    }
}
