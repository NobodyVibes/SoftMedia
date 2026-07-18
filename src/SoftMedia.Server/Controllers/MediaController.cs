using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IMediaRetrievalService _mediaRetrievalService;
    private readonly IRecommendationService _recommendationService;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly IUserContentRatingProvider _ratingProvider;

    public MediaController(
        AppDbContext context,
        IMediaRetrievalService mediaRetrievalService,
        IRecommendationService recommendationService,
        IUserLibraryAccessProvider libraryAccessProvider,
        IUserContentRatingProvider ratingProvider)
    {
        _context = context;
        _mediaRetrievalService = mediaRetrievalService;
        _recommendationService = recommendationService;
        _libraryAccessProvider = libraryAccessProvider;
        _ratingProvider = ratingProvider;
    }



    [HttpGet("{id}")]
    public async Task<ActionResult<MediaItemDto>> GetMediaItem(Guid id)
    {
        // Audit M4: apply the per-user library ACL + content-rating ceiling so a
        // restricted/child account cannot pull full metadata (overview, cast, genres)
        // for an item in a denied or over-rating library by guessing its id. A filtered-
        // out item resolves to null -> 404, matching the anti-probe behaviour elsewhere.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var item = await _context.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Include(m => m.Library)
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Include(m => m.MediaItemCasts).ThenInclude(mc => mc.Person)
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
        // Audit M10: clamp before the repository multiplies it (Take(limit*25)) over a
        // join-heavy query, so a huge limit can't hydrate the whole table into memory.
        limit = Math.Clamp(limit, 1, 100);

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
    /// R-WI-020 — personalized home rows for the calling user. Empty list (200)
    /// when the user has no play history; the client renders nothing.
    /// </summary>
    [HttpGet("home-rows")]
    public async Task<ActionResult<IReadOnlyList<HomeRowDto>>> GetHomeRows([FromQuery] int itemsPerRow = 15)
    {
        var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId)) return Unauthorized();
        return Ok(await _recommendationService.GetHomeRowsAsync(userId, itemsPerRow));
    }

    /// <summary>
    /// Global search across all libraries. Returns results grouped by library.
    /// R-WI-017: matches title/description/genre/cast (top-level items), tracks by
    /// title/artist/album, and episodes by title; title matches rank first.
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

        // Audit M10: clamp before multiplying into the Take() so a huge limit can't blow up memory.
        limit = Math.Clamp(limit, 1, 50);

        // R-WI-017 review: user input must be LITERAL in the LIKE patterns. Raw `%`/`_`
        // are live wildcards — "100%" widened to a prefix match, "_dge" ranked every
        // ~dge title as a "prefix" hit, and a long interleaved-wildcard query is a
        // superlinear-scan DoS vector (amplified 5× by the multi-field expansion).
        // Escaped via the 3-arg Like overload's ESCAPE clause; length capped since no
        // real title/person query needs more.
        query = query.Trim();
        if (query.Length > 100) query = query[..100];
        var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var searchPattern = $"%{escaped}%";
        var prefixPattern = $"{escaped}%";
        const string Esc = "\\";
        var globalLimit = limit * 5; // Bounded global search to prevent explosive memory allocation

        // Wave C — apply per-user library ACL before any other narrowing so
        // pagination/limit math operates on the user's visible set only.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        // R-WI-017 / D-12: every browse path applies the content-rating ceiling —
        // search did not, so a rating-restricted account could surface blocked
        // titles by name (and the multi-field expansion below would have widened
        // that to cast and descriptions).
        var ceilings = await _ratingProvider.GetCurrentAsync();

        // Episodes must not become a side door around a series-level ceiling: an
        // episode row can carry its OWN (permissive) rating while the parent series
        // is above the caller's ceiling — the row-level filter passes it, but the
        // series page it belongs to is blocked (review MED-LOW). Gate episode hits
        // on the parent series ALSO passing the rating filter.
        var ratedSeries = _context.MediaItems.ApplyContentRatingFilter(ceilings);

        // R-WI-017 multi-field matching (LIKE-over-joins per the plan — FTS5 is an
        // explicit follow-up). Field breadth is tiered by type to keep results sane:
        // - top-level items (movies/series/albums/…): title, description, genre,
        //   cast/person, and for albums the artist name;
        // - tracks: title + artist/album names (genre/description would return every
        //   track of every matching album);
        // - episodes: title only (inherited metadata would flood results);
        // - Seasons are newly EXCLUDED (previously title-searchable): "Season 1"
        //   matches every show and carries no information the series hit doesn't.
        var matchingItems = await _context.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .Include(m => m.Library)
            .Include(m => m.Series) // episode poster fallback + seriesTitle name context
            .Include(m => m.Artist) // track/album subtitle context (metadata.artist)
            .Include(m => m.Album)  // track subtitle context (metadata.album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m =>
                (m.Type != MediaType.Episode && m.Type != MediaType.Audio && m.Type != MediaType.Season && (
                    EF.Functions.Like(m.Title, searchPattern, Esc)
                    || (m.Overview != null && EF.Functions.Like(m.Overview, searchPattern, Esc))
                    || m.MediaItemGenres.Any(mg => mg.Genre != null && EF.Functions.Like(mg.Genre.Name, searchPattern, Esc))
                    || m.MediaItemCasts.Any(mc => mc.Person != null && EF.Functions.Like(mc.Person.Name, searchPattern, Esc))
                    || (m.Artist != null && EF.Functions.Like(m.Artist.Title, searchPattern, Esc))))
                || (m.Type == MediaType.Audio && (
                    EF.Functions.Like(m.Title, searchPattern, Esc)
                    || (m.Artist != null && EF.Functions.Like(m.Artist.Title, searchPattern, Esc))
                    || (m.Album != null && EF.Functions.Like(m.Album.Title, searchPattern, Esc))))
                || (m.Type == MediaType.Episode
                    && EF.Functions.Like(m.Title, searchPattern, Esc)
                    && ratedSeries.Any(s => s.Id == m.SeriesId)))
            // Ranking: title-prefix first, then any title match, then other-field matches.
            .OrderBy(m => EF.Functions.Like(m.Title, prefixPattern, Esc) ? 0
                        : EF.Functions.Like(m.Title, searchPattern, Esc) ? 1 : 2)
            .ThenBy(m => m.Library!.Order)
            .ThenBy(m => m.Title)
            .Take(globalLimit)
            .ToListAsync();

        // Group by the library ID, not the entity: plain AsNoTracking materializes a
        // DISTINCT Library instance per row, so reference-keyed grouping put every
        // item in its own single-item group (duplicate library headers in the search
        // dropdown — found live once multi-field matching made result sets bigger).
        var results = matchingItems
            .Where(m => m.Library != null)
            .GroupBy(m => m.LibraryId)
            .Select(g => new GlobalSearchResultDto
            {
                LibraryId = g.Key,
                LibraryName = g.First().Library!.Name,
                LibraryType = g.First().Library!.Type.ToString(),
                Items = g.Take(limit).Select(i => MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy")).ToList()
            })
            .ToList();

        return Ok(results);
    }
}
