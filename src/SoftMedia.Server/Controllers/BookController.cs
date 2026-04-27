using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Serves book-specific endpoints: format info and per-page extraction for comic archives.
/// PDF and EPUB are served via the generic /api/v1/stream/{id} range endpoint — only
/// comic archives need per-page extraction.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/books")]
public class BookController : ControllerBase
{
    private readonly IMediaRepository _mediaRepository;
    private readonly IStreamSecurityService _securityService;
    private readonly IComicArchiveService _comicArchiveService;
    private readonly IComicPageThumbnailService _thumbnails;
    private readonly AppDbContext _context;
    private readonly ILogger<BookController> _logger;

    public BookController(
        IMediaRepository mediaRepository,
        IStreamSecurityService securityService,
        IComicArchiveService comicArchiveService,
        IComicPageThumbnailService thumbnails,
        AppDbContext context,
        ILogger<BookController> logger)
    {
        _mediaRepository = mediaRepository;
        _securityService = securityService;
        _comicArchiveService = comicArchiveService;
        _thumbnails = thumbnails;
        _context = context;
        _logger = logger;
    }

    private Guid GetUserId()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    /// <summary>
    /// Returns the book's format and (for comics) total page count.
    /// </summary>
    [HttpGet("{id}/info")]
    public async Task<ActionResult<BookInfoDto>> GetInfo(Guid id, CancellationToken cancellationToken)
    {
        var item = await _mediaRepository.GetByIdWithLibraryAsync(id);
        var access = _securityService.ValidateMediaAccess(item);
        if (access == MediaAccessResult.FileNotFound || item is null) return NotFound();
        if (access == MediaAccessResult.Unauthorized) return Forbid();

        var ext = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
        int? pageCount = null;

        if (_comicArchiveService.IsSupportedArchive(item.Path))
        {
            try
            {
                pageCount = await _comicArchiveService.GetPageCountAsync(item.Path, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to count pages for comic archive {Path}", item.Path);
            }
        }

        return Ok(new BookInfoDto
        {
            Id = item.Id,
            Format = ext,
            PageCount = pageCount
        });
    }

    /// <summary>
    /// Returns a single page image (1-based) from a comic archive.
    /// </summary>
    [HttpGet("{id}/page/{pageNumber:int}")]
    public async Task<IActionResult> GetPage(Guid id, int pageNumber, CancellationToken cancellationToken)
    {
        if (pageNumber < 1) return BadRequest("pageNumber must be >= 1");

        var item = await _mediaRepository.GetByIdWithLibraryAsync(id);
        var access = _securityService.ValidateMediaAccess(item);
        if (access == MediaAccessResult.FileNotFound || item is null) return NotFound();
        if (access == MediaAccessResult.Unauthorized) return Forbid();

        if (!_comicArchiveService.IsSupportedArchive(item.Path))
        {
            return BadRequest("Per-page extraction is only supported for comic archives (CBZ).");
        }

        try
        {
            var page = await _comicArchiveService.GetPageAsync(item.Path, pageNumber, cancellationToken);
            if (page is null) return NotFound();
            return File(page.Data, page.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract page {Page} from {Path}", pageNumber, item.Path);
            return StatusCode(500, "Failed to read comic archive.");
        }
    }

    // ── ER-032: Page thumbnails (scrubber preview) ───────────────────────────
    // CBZ / CBR only. PDFs render thumbnails client-side via pdf.js off the
    // main thread; the server has no PDF rasteriser and shipping one for a
    // preview feature isn't worth the dependency weight. EPUB has no page
    // images at all. Caching lives in the service; the controller is a thin
    // wrapper that enforces access and validates the query shape.
    [HttpGet("{id}/thumbnail/{pageNumber:int}")]
    public async Task<IActionResult> GetThumbnail(
        Guid id,
        int pageNumber,
        [FromQuery] string? size,
        CancellationToken cancellationToken)
    {
        if (pageNumber < 1) return BadRequest("pageNumber must be >= 1");

        var item = await _mediaRepository.GetByIdWithLibraryAsync(id);
        var access = _securityService.ValidateMediaAccess(item);
        if (access == MediaAccessResult.FileNotFound || item is null) return NotFound();
        if (access == MediaAccessResult.Unauthorized) return Forbid();

        if (!_comicArchiveService.IsSupportedArchive(item.Path))
        {
            return BadRequest("Thumbnails are only available for comic archives (CBZ/CBR).");
        }

        // `size` is a named preset rather than a free pixel value — prevents a
        // malicious client from asking for 8192px thumbs and draining the cache.
        var width = size?.ToLowerInvariant() switch
        {
            "sm" => 160,
            "md" => 240,
            "lg" => 360,
            _ => 160,
        };

        try
        {
            var bytes = await _thumbnails.GetAsync(item.Path, pageNumber, width, cancellationToken);
            if (bytes is null || bytes.Length == 0) return NotFound();
            // Response cache headers: the thumbnail is deterministic for a
            // given (file mtime, page, size), but we don't expose mtime so we
            // keep this short.
            Response.Headers.CacheControl = "private, max-age=900";
            return File(bytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build thumbnail for {Path} page {Page}",
                item.Path, pageNumber);
            return StatusCode(500, "Failed to build thumbnail.");
        }
    }

    // ── ER-023: Bookmarks ────────────────────────────────────────────────────
    // All four endpoints share the same authorisation invariant: a user can
    // only see and mutate bookmarks they created themselves. Ownership is
    // checked by pairing the bookmark's UserId with the caller's claim — there
    // is no separate "public bookmarks" surface.

    /// <summary>List this user's bookmarks for a single book, oldest first.</summary>
    [HttpGet("{id}/bookmarks")]
    public async Task<ActionResult<List<BookmarkDto>>> ListBookmarks(Guid id)
    {
        var userId = GetUserId();
        var rows = await _context.Bookmarks
            .AsNoTracking()
            .Where(b => b.UserId == userId && b.MediaItemId == id)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();

        return Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>
    /// Create a bookmark at the given position (PDF/CBZ) or CFI (EPUB). At least
    /// one of the two must be supplied.
    /// </summary>
    [HttpPost("{id}/bookmarks")]
    public async Task<ActionResult<BookmarkDto>> CreateBookmark(Guid id, [FromBody] CreateBookmarkRequest request)
    {
        var userId = GetUserId();

        // Invariant: exactly one of Position/Cfi. Position nulls imply Cfi
        // present, and vice versa — the client is expected to know the format.
        var hasPosition = request.Position.HasValue && request.Position.Value > 0;
        var hasCfi = !string.IsNullOrWhiteSpace(request.Cfi);
        if (!hasPosition && !hasCfi)
        {
            return BadRequest("Bookmark must specify either a positive Position or a Cfi.");
        }

        var exists = await _context.MediaItems.AnyAsync(m => m.Id == id);
        if (!exists) return NotFound();

        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MediaItemId = id,
            Position = hasPosition ? request.Position : null,
            Cfi = hasCfi ? request.Cfi : null,
            Label = string.IsNullOrWhiteSpace(request.Label)
                ? null
                : request.Label!.Length > 200 ? request.Label!.Substring(0, 200) : request.Label,
            CreatedAt = DateTime.UtcNow,
        };
        _context.Bookmarks.Add(bookmark);
        await _context.SaveChangesAsync();

        return Ok(ToDto(bookmark));
    }

    /// <summary>Rename or relabel a bookmark. Only the label is mutable.</summary>
    [HttpPatch("{id}/bookmarks/{bookmarkId}")]
    public async Task<IActionResult> UpdateBookmark(Guid id, Guid bookmarkId, [FromBody] UpdateBookmarkRequest request)
    {
        var userId = GetUserId();
        var row = await _context.Bookmarks
            .FirstOrDefaultAsync(b => b.Id == bookmarkId && b.UserId == userId && b.MediaItemId == id);
        if (row == null) return NotFound();

        row.Label = string.IsNullOrWhiteSpace(request.Label)
            ? null
            : request.Label!.Length > 200 ? request.Label!.Substring(0, 200) : request.Label;
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("{id}/bookmarks/{bookmarkId}")]
    public async Task<IActionResult> DeleteBookmark(Guid id, Guid bookmarkId)
    {
        var userId = GetUserId();
        var row = await _context.Bookmarks
            .FirstOrDefaultAsync(b => b.Id == bookmarkId && b.UserId == userId && b.MediaItemId == id);
        if (row == null) return NotFound();

        _context.Bookmarks.Remove(row);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static BookmarkDto ToDto(Bookmark b) => new()
    {
        Id = b.Id,
        Position = b.Position,
        Cfi = b.Cfi,
        Label = b.Label,
        CreatedAt = b.CreatedAt,
    };

    // ── ER-040 + ER-041: Highlights ──────────────────────────────────────────
    // Same ownership invariant as bookmarks — caller's claim must match
    // Highlight.UserId or the endpoint 404s. LocationJson is opaque: the
    // server neither parses nor validates shape. Payload size caps live on
    // the entity's [MaxLength] attributes; controller-level checks here give
    // the client a clean 400 instead of a provider exception.

    [HttpGet("{id}/highlights")]
    public async Task<ActionResult<List<HighlightDto>>> ListHighlights(Guid id)
    {
        var userId = GetUserId();
        var rows = await _context.Highlights
            .AsNoTracking()
            .Where(h => h.UserId == userId && h.MediaItemId == id)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("{id}/highlights")]
    public async Task<ActionResult<HighlightDto>> CreateHighlight(Guid id, [FromBody] CreateHighlightRequest request)
    {
        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(request.LocationJson) || request.LocationJson.Length > 4096)
        {
            return BadRequest("LocationJson must be a non-empty JSON payload no larger than 4 KB.");
        }
        if (request.QuotedText != null && request.QuotedText.Length > 8192)
        {
            return BadRequest("QuotedText exceeds the 8 KB limit.");
        }
        if (request.Note != null && request.Note.Length > 8192)
        {
            return BadRequest("Note exceeds the 8 KB limit.");
        }

        var exists = await _context.MediaItems.AnyAsync(m => m.Id == id);
        if (!exists) return NotFound();

        var now = DateTime.UtcNow;
        var highlight = new Highlight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MediaItemId = id,
            LocationJson = request.LocationJson,
            Colour = string.IsNullOrWhiteSpace(request.Colour) ? "yellow" : request.Colour.Trim(),
            QuotedText = request.QuotedText ?? string.Empty,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Highlights.Add(highlight);
        await _context.SaveChangesAsync();

        return Ok(ToDto(highlight));
    }

    [HttpPatch("{id}/highlights/{highlightId}")]
    public async Task<IActionResult> UpdateHighlight(Guid id, Guid highlightId, [FromBody] UpdateHighlightRequest request)
    {
        var userId = GetUserId();
        var row = await _context.Highlights
            .FirstOrDefaultAsync(h => h.Id == highlightId && h.UserId == userId && h.MediaItemId == id);
        if (row == null) return NotFound();

        // Only Colour and Note are mutable. Location / QuotedText are bound to
        // the original selection — changing them would make the highlight
        // float. Users delete and re-create to change location.
        if (request.Colour != null)
        {
            if (request.Colour.Length > 32) return BadRequest("Colour too long.");
            row.Colour = request.Colour.Trim();
        }
        if (request.Note != null)
        {
            if (request.Note.Length > 8192) return BadRequest("Note exceeds the 8 KB limit.");
            row.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note;
        }
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/highlights/{highlightId}")]
    public async Task<IActionResult> DeleteHighlight(Guid id, Guid highlightId)
    {
        var userId = GetUserId();
        var row = await _context.Highlights
            .FirstOrDefaultAsync(h => h.Id == highlightId && h.UserId == userId && h.MediaItemId == id);
        if (row == null) return NotFound();

        _context.Highlights.Remove(row);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static HighlightDto ToDto(Highlight h) => new()
    {
        Id = h.Id,
        LocationJson = h.LocationJson,
        Colour = h.Colour,
        QuotedText = h.QuotedText,
        Note = h.Note,
        CreatedAt = h.CreatedAt,
        UpdatedAt = h.UpdatedAt,
    };
}

// ── ER-023 DTOs ──────────────────────────────────────────────────────────────

public class BookmarkDto
{
    public Guid Id { get; set; }
    public int? Position { get; set; }
    public string? Cfi { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateBookmarkRequest
{
    public int? Position { get; set; }
    public string? Cfi { get; set; }
    public string? Label { get; set; }
}

public class UpdateBookmarkRequest
{
    public string? Label { get; set; }
}

// ── ER-040 / ER-041 DTOs ─────────────────────────────────────────────────────

public class HighlightDto
{
    public Guid Id { get; set; }
    public string LocationJson { get; set; } = "{}";
    public string Colour { get; set; } = "yellow";
    public string QuotedText { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateHighlightRequest
{
    public string? LocationJson { get; set; }
    public string? Colour { get; set; }
    public string? QuotedText { get; set; }
    public string? Note { get; set; }
}

public class UpdateHighlightRequest
{
    public string? Colour { get; set; }
    public string? Note { get; set; }
}
