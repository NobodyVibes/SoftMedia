using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;

    public MediaController(AppDbContext context)
    {
        _context = context;
    }

    private Guid GetUserId()
    {
        var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            // Fallback or throw? For now, we assume authorized due to [Authorize]
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MediaItemDto>> GetMediaItem(Guid id)
    {
        var item = await _context.MediaItems
            .Include(m => m.Library)
            .Include(m => m.Series)
            .Include(m => m.Album)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (item == null)
        {
            return NotFound();
        }

        UserMediaInteraction? interaction = null;
        try 
        {
            var userId = GetUserId();
            interaction = await _context.UserMediaInteractions
                .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == id);
        }
        catch 
        {
            // Ignore if user claim not found (shouldn't happen with [Authorize])
        }

        return MediaItemDto.FromMediaItem(item, "/api/v1/image/proxy", interaction);
    }

    [HttpGet("recent")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetRecentMedia([FromQuery] int limit = 20, [FromQuery] string? type = null)
    {
        IQueryable<MediaItem> query = _context.MediaItems;

        if (!string.IsNullOrEmpty(type) && Enum.TryParse<LibraryType>(type, true, out var libraryType))
        {
            var libraryIds = await _context.Libraries
                .Where(l => l.Type == libraryType)
                .Select(l => l.Id)
                .ToListAsync();

            query = query.Where(m => libraryIds.Contains(m.LibraryId));
        }

        // Fetch more items than limit to allow for collapsing episodes/tracks into series/albums
        var rawLimit = limit * 5;
        
        var rawItems = await query
            .Include(m => m.Series)
            .Include(m => m.Album)
            .OrderByDescending(m => m.DateAdded)
            .Take(rawLimit)
            .ToListAsync();

        var distinctItems = new List<MediaItem>();
        var seenSeries = new HashSet<Guid>();
        var seenAlbums = new HashSet<Guid>();

        foreach (var item in rawItems)
        {
            if (distinctItems.Count >= limit) break;

            if (item.Type == MediaType.Episode && item.Series != null)
            {
                if (!seenSeries.Contains(item.Series.Id))
                {
                    distinctItems.Add(item.Series);
                    seenSeries.Add(item.Series.Id);
                }
            }
            else if (item.Type == MediaType.Audio && item.Album != null)
            {
                if (!seenAlbums.Contains(item.Album.Id))
                {
                     distinctItems.Add(item.Album);
                     seenAlbums.Add(item.Album.Id);
                }
            }
            else
            {
                // For base Series/Album items, we also check if we've already added them via an episode/track
                if (item.Type == MediaType.Series)
                {
                    if (!seenSeries.Contains(item.Id))
                    {
                        distinctItems.Add(item);
                        seenSeries.Add(item.Id);
                    }
                }
                else if (item.Type == MediaType.Album)
                {
                    if (!seenAlbums.Contains(item.Id))
                    {
                        distinctItems.Add(item);
                        seenAlbums.Add(item.Id);
                    }
                }
                else
                {
                    distinctItems.Add(item);
                }
            }
        }

        // Batch fetch interactions for all distinct items
        var itemIds = distinctItems.Select(x => x.Id).ToList();
        var interactions = new Dictionary<Guid, UserMediaInteraction>();
        try
        {
             var userId = GetUserId();
             var interactionList = await _context.UserMediaInteractions
                 .Where(x => x.UserId == userId && itemIds.Contains(x.MediaItemId))
                 .ToListAsync();
             
             foreach(var i in interactionList)
             {
                 interactions[i.MediaItemId] = i;
             }
        }
        catch {}

        return distinctItems.Select(i => {
            interactions.TryGetValue(i.Id, out var interaction);
            return MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy", interaction);
        }).ToList();
    }
}
