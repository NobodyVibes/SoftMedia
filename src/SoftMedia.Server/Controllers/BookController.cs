using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.DTOs;
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
    private readonly ILogger<BookController> _logger;

    public BookController(
        IMediaRepository mediaRepository,
        IStreamSecurityService securityService,
        IComicArchiveService comicArchiveService,
        ILogger<BookController> logger)
    {
        _mediaRepository = mediaRepository;
        _securityService = securityService;
        _comicArchiveService = comicArchiveService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the book's format and (for comics) total page count.
    /// </summary>
    [HttpGet("{id}/info")]
    public async Task<ActionResult<BookInfoDto>> GetInfo(Guid id, CancellationToken cancellationToken)
    {
        var item = await _mediaRepository.GetByIdWithLibraryAsync(id);
        var access = _securityService.ValidateMediaAccess(item!);
        if (access == MediaAccessResult.FileNotFound) return NotFound();
        if (access == MediaAccessResult.Unauthorized) return Forbid();

        var ext = Path.GetExtension(item!.Path).TrimStart('.').ToLowerInvariant();
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
        var access = _securityService.ValidateMediaAccess(item!);
        if (access == MediaAccessResult.FileNotFound) return NotFound();
        if (access == MediaAccessResult.Unauthorized) return Forbid();

        if (!_comicArchiveService.IsSupportedArchive(item!.Path))
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
}
