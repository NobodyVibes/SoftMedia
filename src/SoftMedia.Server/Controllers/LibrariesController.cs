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

    public LibrariesController(AppDbContext context, IFileScannerService fileScanner)
    {
        _context = context;
        _fileScanner = fileScanner;
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

    [HttpPost("{id}/scan")]
    public async Task<IActionResult> ScanLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        // Run in background? Or await? 
        // For now, await to report errors, but ideally should be background job.
        // Given it's a personal server, awaiting is okay for immediate feedback, 
        // but might timeout for large libraries.
        // Better: Fire and forget, or return Accepted.
        // The requirement says "Trigger immediate library scan".
        // I'll use fire-and-forget for the HTTP request but log it.
        // Actually, FileScannerService.ScanLibraryAsync is async.
        // If I await it, the UI hangs.
        // I will run it in a background task.
        _ = Task.Run(async () => 
        {
            try 
            {
                await _fileScanner.ScanLibraryAsync(id);
            }
            catch (Exception ex)
            {
                // Log error (logger not injected in controller, but FileScanner logs internally)
                Console.WriteLine($"Error scanning library {id}: {ex.Message}");
            }
        });

        return Accepted(new { message = "Scan started" });
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
            // Filter by User Rating
            joinedQuery = joinedQuery.Where(x => x.Interaction.Rating >= minRating.Value);
        }

        if (isFavorite.HasValue)
        {
            joinedQuery = joinedQuery.Where(x => x.Interaction.IsFavorite == isFavorite.Value);
        }

        if (watched.HasValue)
        {
            joinedQuery = joinedQuery.Where(x => x.Interaction.IsWatched == watched.Value);
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
        // Get the series item to access its metadata
        var series = await _context.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == seriesId && m.Type == MediaType.Series);

        if (series == null)
        {
            return NotFound("Series not found");
        }

        // Get distinct seasons from episodes
        var seasonNumbers = await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .Select(m => m.SeasonNumber ?? 1)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();

        // Parse seasons from series metadata
        var seasonsFromMetadata = new Dictionary<int, object?>();
        if (!string.IsNullOrEmpty(series.MetadataJson))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
                if (metadata != null && metadata.TryGetValue("seasons", out var seasonsObj) && seasonsObj is System.Text.Json.JsonElement seasonsArray)
                {
                    foreach (var season in seasonsArray.EnumerateArray())
                    {
                        int? num = season.TryGetProperty("number", out var numProp) && numProp.ValueKind != System.Text.Json.JsonValueKind.Null 
                            ? numProp.GetInt32() 
                            : null;
                        
                        if (num.HasValue)
                        {
                            string? poster = season.TryGetProperty("poster", out var posterProp) && posterProp.ValueKind != System.Text.Json.JsonValueKind.Null
                                ? $"/api/v1/image/proxy?url={Uri.EscapeDataString(posterProp.GetString() ?? "")}"
                                : null;
                            
                            int? episodeCount = season.TryGetProperty("episodeCount", out var epCountProp) && epCountProp.ValueKind != System.Text.Json.JsonValueKind.Null
                                ? epCountProp.GetInt32()
                                : null;
                            
                            string? premiereDate = season.TryGetProperty("premiereDate", out var premProp) && premProp.ValueKind != System.Text.Json.JsonValueKind.Null
                                ? premProp.GetString()
                                : null;

                            seasonsFromMetadata[num.Value] = new { number = num.Value, poster, episodeCount, premiereDate };
                        }
                    }
                }
            }
            catch { /* Ignore parsing errors */ }
        }

        // Build result combining episode data with metadata
        var result = seasonNumbers.Select(seasonNum =>
        {
            var episodeCount = _context.MediaItems.AsNoTracking()
                .Count(m => m.SeriesId == seriesId && m.SeasonNumber == seasonNum && m.Type == MediaType.Episode);

            if (seasonsFromMetadata.TryGetValue(seasonNum, out var metaSeason))
            {
                // Use metadata if available (has poster)
                return metaSeason;
            }

            // Fallback to show poster as season poster
            string? fallbackPoster = null;
            if (!string.IsNullOrEmpty(series.MetadataJson))
            {
                try
                {
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
                    if (metadata != null && metadata.TryGetValue("poster", out var posterObj))
                    {
                        fallbackPoster = $"/api/v1/image/proxy?url={Uri.EscapeDataString(posterObj.ToString() ?? "")}";
                    }
                }
                catch { }
            }

            return (object)new { number = seasonNum, poster = fallbackPoster, episodeCount = episodeCount, premiereDate = (string?)null };
        }).ToList();

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
