using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// A folder-derived photo album: photos group by the directory they live in,
/// relative to the library root ("2024/Italy" → album "Italy"). No metadata or
/// schema — the user's own folder structure IS the organization.
/// </summary>
public record PhotoAlbumDto(string Key, string Name, int PhotoCount, Guid CoverPhotoId, DateTime? LatestDate);

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
    private readonly Data.AppDbContext _context;
    private readonly Services.Security.LibraryAccess.IUserLibraryAccessProvider _libraryAccess;
    private readonly ILogger<PhotosController> _logger;

    public PhotosController(
        IMediaRepository mediaRepository,
        IStreamSecurityService streamSecurity,
        IThumbnailService thumbnailService,
        Data.AppDbContext context,
        Services.Security.LibraryAccess.IUserLibraryAccessProvider libraryAccess,
        ILogger<PhotosController> logger)
    {
        _mediaRepository = mediaRepository;
        _streamSecurity = streamSecurity;
        _thumbnailService = thumbnailService;
        _context = context;
        _libraryAccess = libraryAccess;
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

    /// <summary>
    /// Folder-derived albums for a photo library, newest first. The cover is the
    /// album's most recent photo. Unknown/denied/non-photo libraries 404 (anti-probe).
    /// </summary>
    [HttpGet("albums")]
    public async Task<ActionResult<List<PhotoAlbumDto>>> GetAlbums([FromQuery] Guid libraryId)
    {
        var library = await GetAccessiblePhotoLibraryAsync(libraryId);
        if (library == null) return NotFound();

        var photos = await _context.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Photo)
            .Select(m => new { m.Id, m.Path, m.ReleaseDate, m.DateAdded })
            .ToListAsync();

        var albums = photos
            .GroupBy(p => AlbumKeyFor(p.Path, library.Paths))
            .Select(g =>
            {
                var cover = g.OrderByDescending(p => p.ReleaseDate ?? p.DateAdded).First();
                return new PhotoAlbumDto(
                    g.Key,
                    AlbumNameFor(g.Key),
                    g.Count(),
                    cover.Id,
                    g.Max(p => p.ReleaseDate ?? p.DateAdded));
            })
            .OrderByDescending(a => a.LatestDate)
            .ToList();

        return Ok(albums);
    }

    /// <summary>
    /// Photos in chronological order (date taken, falling back to date added; `sortDir=desc`
    /// flips to newest-first). <paramref name="key"/> is the album key from
    /// <see cref="GetAlbums"/> — "" is the library root's loose photos; OMITTING the
    /// parameter searches across the whole library (the search-results view).
    /// Photo-specialised filters: <paramref name="search"/> (title contains),
    /// <paramref name="camera"/> (EXIF camera), <paramref name="year"/> (year taken).
    /// </summary>
    [HttpGet("albums/photos")]
    public async Task<ActionResult<List<MediaItemDto>>> GetAlbumPhotos(
        [FromQuery] Guid libraryId,
        [FromQuery] string? key = null,
        [FromQuery] string? search = null,
        [FromQuery] string? camera = null,
        [FromQuery] int? year = null,
        [FromQuery] string? sortDir = null)
    {
        var library = await GetAccessiblePhotoLibraryAsync(libraryId);
        if (library == null) return NotFound();

        var photos = await _context.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Photo)
            .ToListAsync();

        IEnumerable<MediaItem> filtered = photos;
        if (key != null)
        {
            filtered = filtered.Where(p => AlbumKeyFor(p.Path, library.Paths) == key);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(p => p.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(camera))
        {
            filtered = filtered.Where(p => string.Equals(CameraOf(p), camera, StringComparison.OrdinalIgnoreCase));
        }
        if (year.HasValue)
        {
            filtered = filtered.Where(p => (p.ReleaseDate ?? p.DateAdded).Year == year.Value);
        }

        var ordered = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase)
            ? filtered.OrderByDescending(p => p.ReleaseDate ?? p.DateAdded)
                .ThenByDescending(p => p.SortTitle, StringComparer.OrdinalIgnoreCase)
            : filtered.OrderBy(p => p.ReleaseDate ?? p.DateAdded)
                .ThenBy(p => p.SortTitle, StringComparer.OrdinalIgnoreCase);

        return Ok(ordered.Select(p => MediaItemDto.FromMediaItem(p)).ToList());
    }

    /// <summary>
    /// Facet values for the photo filter bar: distinct EXIF cameras (alphabetical) and
    /// years with photos (newest first). Empty facets mean the control has nothing to
    /// offer and the client hides it.
    /// </summary>
    [HttpGet("filters")]
    public async Task<ActionResult<object>> GetFilterFacets([FromQuery] Guid libraryId)
    {
        var library = await GetAccessiblePhotoLibraryAsync(libraryId);
        if (library == null) return NotFound();

        var photos = await _context.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Photo)
            .Select(m => new { m.ExifJson, m.ReleaseDate, m.DateAdded })
            .ToListAsync();

        var cameras = photos
            .Select(p => CameraFromExifJson(p.ExifJson))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var years = photos
            .Select(p => (p.ReleaseDate ?? p.DateAdded).Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        return Ok(new { cameras, years });
    }

    /// <summary>The EXIF camera string ("Make Model") persisted by PhotoExifReader, or null.</summary>
    private static string? CameraOf(MediaItem photo) => CameraFromExifJson(photo.ExifJson);

    private static string? CameraFromExifJson(string? exifJson)
    {
        if (string.IsNullOrEmpty(exifJson)) return null;
        try
        {
            var exif = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(exifJson);
            return exif != null && exif.TryGetValue("camera", out var cam) ? cam : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt row: the photo just has no camera facet
        }
    }

    /// <summary>Resolves the library iff it exists, is a photo library, and the caller's
    /// per-user ACL admits it — every failure mode collapses to null → 404.</summary>
    private async Task<Library?> GetAccessiblePhotoLibraryAsync(Guid libraryId)
    {
        var library = await _context.Libraries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == libraryId && l.Type == LibraryType.Photo);
        if (library == null) return null;

        var access = await _libraryAccess.GetCurrentAsync();
        if (!access.IsUnrestricted && !access.AllowedLibraryIds.Contains(libraryId)) return null;

        return library;
    }

    /// <summary>
    /// Album key = the photo's directory relative to its library root, '/'-normalized
    /// ("2024/Italy"); photos directly in a root key to "". Multi-root libraries with
    /// identical relative folders merge — the folder name is the identity, by design.
    /// </summary>
    // Public so tests can drive it directly (project convention; no InternalsVisibleTo).
    public static string AlbumKeyFor(string photoPath, List<string> libraryRoots)
    {
        var dir = (Path.GetDirectoryName(photoPath) ?? "").Replace('\\', '/').TrimEnd('/');
        foreach (var root in libraryRoots)
        {
            var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
            if (dir.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return "";
            if (dir.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                return dir[(normalizedRoot.Length + 1)..];
        }
        // Root mismatch (library path edited since scan): fall back to the leaf folder
        // so the photo still lands in a sensibly-named album instead of vanishing.
        return dir.Split('/').LastOrDefault() ?? "";
    }

    public static string AlbumNameFor(string key) =>
        string.IsNullOrEmpty(key) ? "Unsorted" : key.Split('/').Last();
}
