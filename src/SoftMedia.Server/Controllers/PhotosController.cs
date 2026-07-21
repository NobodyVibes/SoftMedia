using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Serves photo-library images. The photo file IS its own artwork: cards request a WebP
/// thumbnail via <c>?width=</c>; the detail view requests the original (no width).
/// Route is in <c>ServiceCollectionExtensions.IsMediaRoute</c> so &lt;img&gt; tags can
/// authenticate with a reduced-privilege media token in the query string (WS-6).
/// </summary>
[ApiController]
[Authorize(Policy = ScopePolicies.ReadLibrary)] // B-18: photos = catalog data
[Route("api/v1/photos")]
public class PhotosController : ControllerBase
{
    private const int MinThumbnailWidth = 64;
    private const int MaxThumbnailWidth = 800;

    private readonly IMediaRepository _mediaRepository;
    private readonly IStreamSecurityService _streamSecurity;
    private readonly IThumbnailService _thumbnailService;
    private readonly ILogger<PhotosController> _logger;

    public PhotosController(
        IMediaRepository mediaRepository,
        IStreamSecurityService streamSecurity,
        IThumbnailService thumbnailService,
        ILogger<PhotosController> logger)
    {
        _mediaRepository = mediaRepository;
        _streamSecurity = streamSecurity;
        _thumbnailService = thumbnailService;
        _logger = logger;
    }

    /// <summary>
    /// Serve a photo, optionally as a resized WebP thumbnail via <c>?width=</c>
    /// (64–800px). Denials return 404, not 403, per SDD §6.2's anti-probe rule.
    /// </summary>
    [HttpGet("{id:guid}/image")]
    public async Task<IActionResult> GetImage(Guid id, [FromQuery] int? width)
    {
        // Per-user library ACL gate (repository resolves denied libraries to null).
        var item = await _mediaRepository.GetByIdWithLibraryAsync(id);
        if (item == null || item.Type != MediaType.Photo)
        {
            return NotFound();
        }

        // File existence + symlink-resolved library path jail + ACL re-check.
        var access = await _streamSecurity.ValidateMediaAccessAsync(item);
        if (access != MediaAccessResult.Allowed)
        {
            _logger.LogDebug("Photo access denied for {Id}: {Result}", id, access);
            return NotFound();
        }

        var servePath = item.Path;
        var serveMime = MimeTypeResolver.GetMimeType(item.Path);

        if (width.HasValue && width.Value >= MinThumbnailWidth && width.Value <= MaxThumbnailWidth)
        {
            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(
                item.Path, item.Id, width.Value);
            if (thumbPath != null)
            {
                servePath = thumbPath;
                serveMime = "image/webp";
            }
            // Thumbnail failure (e.g. HEIC — no SkiaSharp codec) falls through to the
            // original file; the client's image error fallback handles undisplayable formats.
        }

        var lastModified = System.IO.File.GetLastWriteTimeUtc(servePath);
        Response.Headers["ETag"] = $"\"{lastModified.Ticks}\"";
        Response.Headers["Last-Modified"] = lastModified.ToString("R");

        return PhysicalFile(servePath, serveMime, enableRangeProcessing: true);
    }
}
