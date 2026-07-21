using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

// B-18: library browse/metadata requires the read:library scope for API tokens;
// admin-only endpoints additionally stack their Roles=Admin gate.
[Authorize(Policy = ScopePolicies.ReadLibrary)]
[ApiController]
[Route("api/v1/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly ILibraryService _libraryService;

    public LibrariesController(ILibraryService libraryService)
    {
        _libraryService = libraryService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Library>>> GetLibraries()
    {
        var libs = await _libraryService.GetLibrariesAsync();
        return Ok(libs);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Library>> GetLibrary(Guid id)
    {
        var library = await _libraryService.GetLibraryAsync(id);
        if (library == null) return NotFound();
        return Ok(library);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<Library>> CreateLibrary(CreateLibraryRequest request)
    {
        try
        {
            var library = await _libraryService.CreateLibraryAsync(request);
            return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, library);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateLibrary(Guid id, UpdateLibraryRequest request)
    {
        try
        {
            await _libraryService.UpdateLibraryAsync(id, request);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteLibrary(Guid id)
    {
        // 404 for "nothing deleted" — a silent 204 told the admin a delete succeeded
        // when the id was stale (double-click, out-of-date list), hiding real state.
        var deleted = await _libraryService.DeleteLibraryAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("reorder")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReorderLibraries([FromBody] List<Guid> orderedIds)
    {
        await _libraryService.ReorderLibrariesAsync(orderedIds);
        return NoContent();
    }

    [HttpPost("{id}/scan")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LibraryScanJob>> ScanLibrary(Guid id)
    {
        try
        {
            var job = await _libraryService.ScanLibraryAsync(id);
            return Accepted(job);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("scan-queue")]
    public ActionResult<IEnumerable<LibraryScanJob>> GetScanQueue()
    {
        return Ok(_libraryService.GetScanQueue());
    }

    [HttpGet("scan-jobs/{jobId}")]
    public ActionResult<LibraryScanJob> GetScanJobStatus(Guid jobId)
    {
        var job = _libraryService.GetScanJobStatus(jobId);
        if (job == null) return NotFound();
        return Ok(job);
    }

    [HttpGet("{id}/genres")]
    public async Task<ActionResult<IEnumerable<string>>> GetLibraryGenres(Guid id)
    {
        var genres = await _libraryService.GetLibraryGenresAsync(id);
        return Ok(genres);
    }

    [HttpGet("{id}/recent")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetRecentlyAdded(Guid id)
    {
        var items = await _libraryService.GetRecentlyAddedAsync(id, GetUserId());
        return Ok(items);
    }

    [HttpGet("{id}/items")]
    public async Task<ActionResult<PagedResult<MediaItemDto>>> GetLibraryItems(
        Guid id, 
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? genre = null,
        [FromQuery] int? year = null,
        [FromQuery] int? minRating = null,
        [FromQuery] bool? isFavorite = null,
        [FromQuery] bool? watched = null,
        [FromQuery] string? viewMode = null)
    {
        // Audit M8: clamp paging so a caller can't request millions of rows in one query
        // and exhaust server memory (the repository eagerly Includes joins).
        var filter = new LibraryItemFilter
        {
            Page = Math.Max(page, 1),
            PageSize = Math.Clamp(pageSize, 1, 100),
            Search = search,
            SortBy = sortBy,
            SortDir = sortDir,
            Genre = genre,
            Year = year,
            MinRating = minRating,
            IsFavorite = isFavorite,
            Watched = watched,
            ViewMode = viewMode,
            UserId = GetUserId()
        };

        var result = await _libraryService.GetLibraryItemsAsync(id, filter);
        return Ok(result);
    }

    [HttpGet("series/{seriesId}/episodes")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetSeriesEpisodes(Guid seriesId)
    {
        var episodes = await _libraryService.GetSeriesEpisodesAsync(seriesId, GetUserId());
        return Ok(episodes);
    }

    [HttpGet("series/{seriesId}/seasons")]
    public async Task<ActionResult<IEnumerable<object>>> GetSeriesSeasons(Guid seriesId)
    {
        var seasons = await _libraryService.GetSeriesSeasonsAsync(seriesId);
        return Ok(seasons);
    }

    [HttpGet("comics/{seriesId}/issues")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetComicIssues(Guid seriesId)
    {
        var issues = await _libraryService.GetComicIssuesAsync(seriesId, GetUserId());
        return Ok(issues);
    }

    [HttpGet("artists/{artistId}/albums")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetArtistAlbums(Guid artistId)
    {
        var albums = await _libraryService.GetArtistAlbumsAsync(artistId, GetUserId());
        return Ok(albums);
    }

    [HttpGet("albums/{albumId}/tracks")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetAlbumTracks(Guid albumId)
    {
        var tracks = await _libraryService.GetAlbumTracksAsync(albumId, GetUserId());
        return Ok(tracks);
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;
    }
}
