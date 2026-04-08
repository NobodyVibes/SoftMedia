using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IMediaRetrievalService _mediaRetrievalService;
    private readonly IRecommendationService _recommendationService;

    public MediaController(
        AppDbContext context, 
        IMediaRetrievalService mediaRetrievalService,
        IRecommendationService recommendationService)
    {
        _context = context;
        _mediaRetrievalService = mediaRetrievalService;
        _recommendationService = recommendationService;
    }



    [HttpGet("{id}")]
    public async Task<ActionResult<MediaItemDto>> GetMediaItem(Guid id)
    {
        var item = await _context.MediaItems
            .Include(m => m.Library)
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (item == null)
        {
            return NotFound();
        }

        UserMediaInteraction? interaction = null;
        try 
        {
            var userId = User.GetUserId();
            interaction = await _context.UserMediaInteractions
                .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == id);
        }
        catch 
        {
            // Ignore if user claim not found (shouldn't happen with [Authorize])
        }

        return MediaItemDto.FromMediaItem(item, "/api/v1/image/proxy", interaction);
    }

    [HttpGet("hero")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetHeroItems()
    {
        var dtos = (await _recommendationService.GetHeroItemsAsync()).ToList();
        
        // Hydrate with user-specific data
        try
        {
            var userId = User.GetUserId();
            var itemIds = dtos.Select(d => d.Id).ToList();
            var interactions = await _context.UserMediaInteractions
                .AsNoTracking()
                .Where(x => x.UserId == userId && itemIds.Contains(x.MediaItemId))
                .ToDictionaryAsync(x => x.MediaItemId);

            foreach (var dto in dtos)
            {
                if (interactions.TryGetValue(dto.Id, out var interaction))
                {
                    dto.PersonalRating = interaction.Rating;
                    dto.IsFavorite = interaction.IsFavorite;
                    dto.Watched = interaction.IsWatched;
                    dto.PlaybackPosition = interaction.PlaybackPosition;
                }
            }
        }
        catch
        {
            // Fallback to average only if user/interaction fails
        }

        return Ok(dtos);
    }

    [HttpGet("recent")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetRecentMedia([FromQuery] int limit = 20, [FromQuery] string? type = null)
    {
        LibraryType? libraryType = null;
        if (!string.IsNullOrEmpty(type) && Enum.TryParse<LibraryType>(type, true, out var parsedType))
        {
            libraryType = parsedType;
        }

        var distinctItems = await _mediaRetrievalService.GetRecentMediaAsync(limit, libraryType);

        // Batch fetch interactions for all distinct items
        var itemIds = distinctItems.Select(x => x.Id).ToList();
        var interactions = new Dictionary<Guid, UserMediaInteraction>();
        try
        {
             var userId = User.GetUserId();
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

    /// <summary>
    /// Global search across all libraries. Returns results grouped by library.
    /// Only returns top-level items (Movies, Series, Albums, etc.) - not episodes or tracks.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<GlobalSearchResultDto>>> GlobalSearch(
        [FromQuery] string query,
        [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return Ok(new List<GlobalSearchResultDto>());
        }

        var searchTerm = query.ToLower();
        var libraries = await _context.Libraries.OrderBy(l => l.Order).ToListAsync();
        var results = new List<GlobalSearchResultDto>();

        // Types to exclude from search results (child items)
        var excludedTypes = new[] { MediaType.Episode, MediaType.Audio };

        foreach (var library in libraries)
        {
            var libraryItems = await _context.MediaItems
                .AsNoTracking()
                .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
                .Where(m => m.LibraryId == library.Id)
                .Where(m => !excludedTypes.Contains(m.Type))
                .Where(m => m.Title.ToLower().Contains(searchTerm))
                .OrderBy(m => m.Title)
                .Take(limit)
                .ToListAsync();

            if (libraryItems.Count > 0)
            {
                results.Add(new GlobalSearchResultDto
                {
                    LibraryId = library.Id,
                    LibraryName = library.Name,
                    LibraryType = library.Type.ToString(),
                    Items = libraryItems.Select(i => MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy")).ToList()
                });
            }
        }

        return Ok(results);
    }
}
