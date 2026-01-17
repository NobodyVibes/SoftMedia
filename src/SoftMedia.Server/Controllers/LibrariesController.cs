using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IFileScannerService _fileScanner;
    private readonly ILibraryScanQueueService _scanQueueService;
    private readonly ImageCacheService _imageCacheService;

    public LibrariesController(
        AppDbContext context, 
        IFileScannerService fileScanner, 
        ILibraryScanQueueService scanQueueService,
        ImageCacheService imageCacheService)
    {
        _context = context;
        _fileScanner = fileScanner;
        _scanQueueService = scanQueueService;
        _imageCacheService = imageCacheService;
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
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound("Library not found.");
        }

        // Get all MetadataJson values for items in this library
        var metadataJsons = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == id && m.MetadataJson != null)
            .Select(m => m.MetadataJson)
            .ToListAsync();

        var allGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var json in metadataJsons)
        {
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("genres", out var genresElement) &&
                    genresElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var genre in genresElement.EnumerateArray())
                    {
                        var genreStr = genre.GetString();
                        if (!string.IsNullOrWhiteSpace(genreStr))
                        {
                            allGenres.Add(genreStr.Trim());
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Skip invalid JSON
            }
        }

        return Ok(allGenres.OrderBy(g => g));
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
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound("Library not found.");
        }

        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var query = _context.MediaItems.AsNoTracking().Where(m => m.LibraryId == id);

        // TV Library: Show only Series
        if (library.Type == LibraryType.TV)
        {
            query = query.Where(m => m.Type == MediaType.Series);
        }
        // Music Library: Handle View Modes
        else if (library.Type == LibraryType.Music)
        {
            if (viewMode == "artists")
            {
                query = query.Where(m => m.Type == MediaType.Artist);
            }
            else if (viewMode == "albums")
            {
                query = query.Where(m => m.Type == MediaType.Album);
            }
            else // Default to Songs/Tracks or Albums depending on preference? Let's default to Albums for now as it's cleaner, or Songs?
                 // Actually, usually "Songs" view shows all tracks.
            {
                // If no view mode, maybe default to Albums? Or Songs?
                // Let's default to Albums if not specified, or handle "songs" explicitly.
                if (viewMode == "songs")
                {
                    query = query.Where(m => m.Type == MediaType.Audio);
                }
                else 
                {
                    // Default to Albums for Music library root
                    query = query.Where(m => m.Type == MediaType.Album);
                }
            }
        }

        // Join with User Interactions
        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userGuid } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        // Filtering
        if (!string.IsNullOrWhiteSpace(search))
        {
            joinedQuery = joinedQuery.Where(x => x.Media.Title.ToLower().Contains(search.ToLower()));
        }

        if (year.HasValue)
        {
            joinedQuery = joinedQuery.Where(x => x.Media.Year == year.Value);
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            // Simple JSON string match for SQLite
            joinedQuery = joinedQuery.Where(x => x.Media.MetadataJson != null && x.Media.MetadataJson.Contains(genre));
        }

        if (minRating.HasValue)
        {
            // Filter by User Rating - only items with an interaction that has a rating >= minRating
            joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.Rating >= minRating.Value);
        }

        if (isFavorite.HasValue)
        {
            if (isFavorite.Value)
            {
                // Show only favorites - must have interaction and be favorited
                joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.IsFavorite == true);
            }
            else
            {
                // Show non-favorites - no interaction or not favorited
                joinedQuery = joinedQuery.Where(x => x.Interaction == null || x.Interaction.IsFavorite == false);
            }
        }

        if (watched.HasValue)
        {
            if (watched.Value)
            {
                // Show only watched - must have interaction and be watched
                joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.IsWatched == true);
            }
            else
            {
                // Show unwatched - no interaction or not watched
                joinedQuery = joinedQuery.Where(x => x.Interaction == null || x.Interaction.IsWatched == false);
            }
        }

        // Sorting
        joinedQuery = sortBy?.ToLower() switch
        {
            "title" => joinedQuery.OrderBy(x => x.Media.Title),
            "dateadded" => joinedQuery.OrderByDescending(x => x.Media.DateAdded),
            "year" => joinedQuery.OrderByDescending(x => x.Media.Year),
            "rating" => joinedQuery.OrderByDescending(x => x.Interaction.Rating), // User Rating
            _ => joinedQuery.OrderBy(x => x.Media.Title)
        };

        var totalCount = await joinedQuery.CountAsync();
        var items = await joinedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var dtos = items.Select(x => 
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

        return new PagedResult<MediaItemDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    [HttpGet("series/{seriesId}/episodes")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetSeriesEpisodes(Guid seriesId)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid userGuid = userIdClaim != null ? Guid.Parse(userIdClaim) : Guid.Empty;

        var query = _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId)
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
        // New Hierarchical Logic: Fetch Season entities directly
        var seasons = await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Season)
            .OrderBy(m => m.SeasonNumber)
            .ToListAsync();

        if (seasons.Count == 0)
        {
            // Fallback for legacy/non-migrated data: Use distinct query
            var seasonNumbers = await _context.MediaItems.AsNoTracking()
                .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
                .Select(m => m.SeasonNumber ?? 1)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync();

            if (seasonNumbers.Count > 0)
            {
                // Try to get series poster for fallback
                var series = await _context.MediaItems.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == seriesId);
                string? showPoster = null;
                 if (series != null && !string.IsNullOrEmpty(series.MetadataJson))
                 {
                    try {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
                        if (meta != null && meta.TryGetValue("poster", out var p)) showPoster = p.ToString();
                     } catch {}
                 }

                 // Standardize fallback poster URL
                 if (!string.IsNullOrEmpty(showPoster) && showPoster.StartsWith("http"))
                     showPoster = $"/api/v1/image/proxy?url={Uri.EscapeDataString(showPoster)}";

                return Ok(seasonNumbers.Select(num => new { 
                    number = num, 
                    poster = showPoster, 
                    episodeCount = _context.MediaItems.Count(e => e.SeriesId == seriesId && e.SeasonNumber == num && e.Type == MediaType.Episode),
                    premiereDate = (string?)null
                }));
            }
        }

        var result = new List<object>();

        foreach (var season in seasons)
        {
            // Extract metadata if available
            string? poster = null;
            string? premiereDate = null;
            int? episodeCount = null;

            if (!string.IsNullOrEmpty(season.MetadataJson))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(season.MetadataJson);
                    if (meta != null)
                    {
                        if (meta.TryGetValue("poster", out var p) && p != null)
                        {
                            // Use the new standard image endpoint for the season entity itself
                            // The ImageController will handle checking MediaImages or MetadataJson
                            poster = $"/api/v1/items/{season.Id}/images/poster";
                        }
                        if (meta.TryGetValue("premiereDate", out var pd) && pd != null) premiereDate = pd.ToString();
                        if (meta.TryGetValue("episodeCount", out var ec) && ec is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Number) episodeCount = el.GetInt32();
                    }
                }
                catch { }
            }

            // Real-time episode count is more accurate
            var realCount = await _context.MediaItems.CountAsync(e => e.SeriesId == seriesId && e.SeasonNumber == season.SeasonNumber && e.Type == MediaType.Episode);
            
            result.Add(new
            {
                id = season.Id, // Expose ID now that it's an entity
                number = season.SeasonNumber,
                poster = poster,
                episodeCount = realCount > 0 ? realCount : episodeCount,
                premiereDate = premiereDate,
                overview = season.Overview
            });
        }

        return Ok(result);
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
