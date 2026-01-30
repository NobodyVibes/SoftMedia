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

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILibraryScanQueueService _scanQueueService;
    private readonly ImageCacheService _imageCacheService;
    private readonly LibraryWatcher _libraryWatcher;
    private readonly ILibraryService _libraryService;

    public LibrariesController(
        AppDbContext context, 
        ILibraryScanQueueService scanQueueService,
        ImageCacheService imageCacheService,
        LibraryWatcher libraryWatcher,
        ILibraryService libraryService)
    {
        _context = context;
        _scanQueueService = scanQueueService;
        _imageCacheService = imageCacheService;
        _libraryWatcher = libraryWatcher;
        _libraryService = libraryService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Library>>> GetLibraries()
    {
        return await _context.Libraries.OrderBy(l => l.Order).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Library>> GetLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);

        if (library == null)
        {
            return NotFound();
        }

        return library;
    }

    [HttpPost]
    public async Task<ActionResult<Library>> CreateLibrary(CreateLibraryRequest request)
    {
        // Validate paths exist
        foreach (var path in request.Paths)
        {
            if (!Directory.Exists(path))
            {
                return BadRequest($"Directory does not exist: {path}");
            }
        }

        // Validate duplicate paths across libraries
        var allLibraries = await _context.Libraries.ToListAsync();
        foreach (var lib in allLibraries)
        {
            foreach (var path in request.Paths)
            {
                if (lib.Paths.Contains(path))
                {
                    return BadRequest($"Path '{path}' is already used by library '{lib.Name}'.");
                }
            }
        }

        var library = new Library
        {
            Name = request.Name,
            Type = request.Type,
            Paths = request.Paths,
            Order = await _context.Libraries.CountAsync() // Add to end
        };

        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        // Automatically trigger an initial scan for the new library
        _scanQueueService.EnqueueScan(library.Id, library.Name);


        return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, library);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateLibrary(Guid id, UpdateLibraryRequest request)
    {
        var library = await _context.Libraries.FindAsync(id);

        if (library == null)
        {
            return NotFound();
        }

        // Validate paths exist
        foreach (var path in request.Paths)
        {
            if (!Directory.Exists(path))
            {
                return BadRequest($"Directory does not exist: {path}");
            }
        }

        // Validate duplicate paths across libraries (excluding current)
        var otherLibraries = await _context.Libraries.Where(l => l.Id != id).ToListAsync();
        foreach (var lib in otherLibraries)
        {
            foreach (var path in request.Paths)
            {
                if (lib.Paths.Contains(path))
                {
                    return BadRequest($"Path '{path}' is already used by library '{lib.Name}'.");
                }
            }
        }

        library.Name = request.Name;
        library.Type = request.Type;
        library.Paths = request.Paths;

        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        // Remove file watchers for this library
        _libraryWatcher.RemoveWatchersForLibrary(id);

        // Get all media items with their types for image cleanup
        var mediaItemsToCleanup = await _context.MediaItems
            .Where(m => m.LibraryId == id)
            .Select(m => new { m.Id, m.Type })
            .ToListAsync();

        // Clean up cached images for all media items in this library
        _imageCacheService.DeleteImagesForLibrary(
            mediaItemsToCleanup.Select(m => (m.Id, m.Type)));

        _context.Libraries.Remove(library);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderLibraries([FromBody] List<Guid> orderedIds)
    {
        var libraries = await _context.Libraries.ToListAsync();
        
        foreach (var library in libraries)
        {
            var index = orderedIds.IndexOf(library.Id);
            if (index != -1)
            {
                library.Order = index;
            }
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Enqueue a library scan and return the job for progress tracking.
    /// </summary>
    [HttpPost("{id}/scan")]
    public async Task<ActionResult<LibraryScanJob>> ScanLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        // Check if already in queue
        if (_scanQueueService.IsLibraryInQueue(id))
        {
            var existingJob = _scanQueueService.GetAllJobs()
                .FirstOrDefault(j => j.LibraryId == id && 
                    (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));
            
            if (existingJob != null)
            {
                return Ok(existingJob); // Return existing job instead of creating duplicate
            }
        }

        // Enqueue the scan job
        var job = _scanQueueService.EnqueueScan(id, library.Name);
        
        return Accepted(job);
    }

    /// <summary>
    /// Get all active and queued scan jobs.
    /// </summary>
    [HttpGet("scan-queue")]
    public ActionResult<IEnumerable<LibraryScanJob>> GetScanQueue()
    {
        var jobs = _scanQueueService.GetAllJobs();
        return Ok(jobs);
    }

    /// <summary>
    /// Get the status of a specific scan job.
    /// </summary>
    [HttpGet("scan-jobs/{jobId}")]
    public ActionResult<LibraryScanJob> GetScanJobStatus(Guid jobId)
    {
        var job = _scanQueueService.GetJobStatus(jobId);
        if (job == null)
        {
            return NotFound();
        }
        return Ok(job);
    }

    /// <summary>
    /// Get all unique genres for a library.
    /// </summary>
    [HttpGet("{id}/genres")]
    public async Task<ActionResult<IEnumerable<string>>> GetLibraryGenres(Guid id)
    {
        // Use Service for complex logic
        var genres = await _libraryService.GetLibraryGenresAsync(id);
        return Ok(genres);
    }

    [HttpGet("{id}/items")]
    public async Task<ActionResult<PagedResult<MediaItemDto>>> GetLibraryItems(
        Guid id, 
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? genre = null,
        [FromQuery] int? year = null,
        [FromQuery] int? minRating = null,
        [FromQuery] bool? isFavorite = null,
        [FromQuery] bool? watched = null,
        [FromQuery] string? viewMode = null)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var filter = new LibraryItemFilter
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            Genre = genre,
            Year = year,
            MinRating = minRating,
            IsFavorite = isFavorite,
            Watched = watched,
            ViewMode = viewMode,
            UserId = userGuid
        };

        var result = await _libraryService.GetLibraryItemsAsync(id, filter);
        return Ok(result);
    }

    [HttpGet("series/{seriesId}/episodes")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetSeriesEpisodes(Guid seriesId)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var query = _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userGuid } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();

        return items.Select(x => 
        {
            var dto = MediaItemDto.FromMediaItem(x.Media, "/api/v1/image/proxy");
             if (x.Interaction != null)
            {
                dto.UserRating = x.Interaction.Rating;
                dto.IsFavorite = x.Interaction.IsFavorite;
                dto.Watched = x.Interaction.IsWatched;
                dto.PlaybackPosition = x.Interaction.PlaybackPosition;
                
                // Calculate progress percentage based on playback position and duration
                if (x.Interaction.PlaybackPosition > 0 && x.Media.Duration > 0)
                {
                    dto.Progress = (x.Interaction.PlaybackPosition / x.Media.Duration) * 100;
                }
            }
            return dto;
        }).ToList();
    }

    [HttpGet("series/{seriesId}/seasons")]
    public async Task<ActionResult<IEnumerable<object>>> GetSeriesSeasons(Guid seriesId)
    {
        var seasons = await _libraryService.GetSeriesSeasonsAsync(seriesId);
        return Ok(seasons);
    }

    [HttpGet("artists/{artistId}/albums")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetArtistAlbums(Guid artistId)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var query = _context.MediaItems.AsNoTracking()
            .Where(m => m.ArtistId == artistId && m.Type == MediaType.Album)
            .OrderByDescending(m => m.Year)
            .ThenBy(m => m.Title);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userGuid } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();

        return items.Select(x => 
        {
            var dto = MediaItemDto.FromMediaItem(x.Media, "/api/v1/image/proxy");
             if (x.Interaction != null)
            {
                dto.UserRating = x.Interaction.Rating;
                dto.IsFavorite = x.Interaction.IsFavorite;
                dto.Watched = x.Interaction.IsWatched;
            }
            return dto;
        }).ToList();
    }

    [HttpGet("albums/{albumId}/tracks")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetAlbumTracks(Guid albumId)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var query = _context.MediaItems.AsNoTracking()
            .Where(m => m.AlbumId == albumId && m.Type == MediaType.Audio)
            .OrderBy(m => m.DiscNumber)
            .ThenBy(m => m.TrackNumber);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userGuid } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();

        return items.Select(x => 
        {
            var dto = MediaItemDto.FromMediaItem(x.Media, "/api/v1/image/proxy");
             if (x.Interaction != null)
            {
                dto.UserRating = x.Interaction.Rating;
                dto.IsFavorite = x.Interaction.IsFavorite;
                dto.Watched = x.Interaction.IsWatched;
            }
            return dto;
        }).ToList();
    }
    [HttpGet("{id}/debug"), AllowAnonymous]
    public async Task<ActionResult> GetLibraryDebug(Guid id)
    {
        var items = await _context.MediaItems
            .Where(m => m.LibraryId == id)
            .Select(m => new { m.Id, m.Title, m.Path, m.MetadataJson, m.Type, m.SeriesId, m.SeasonNumber, m.EpisodeNumber })
            .ToListAsync();
        return Ok(items);
    }
}
