using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Controllers;

// B-18: catalog metadata requires the read:library scope for API tokens
// (full sessions are unaffected — scopes only constrain tokens).
[Authorize(Policy = ScopePolicies.ReadLibrary)]
[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IMediaRetrievalService _mediaRetrievalService;
    private readonly IRecommendationService _recommendationService;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly IUserContentRatingProvider _ratingProvider;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        AppDbContext context,
        IMediaRetrievalService mediaRetrievalService,
        IRecommendationService recommendationService,
        IUserLibraryAccessProvider libraryAccessProvider,
        IUserContentRatingProvider ratingProvider,
        ILogger<MediaController> logger)
    {
        _context = context;
        _mediaRetrievalService = mediaRetrievalService;
        _recommendationService = recommendationService;
        _libraryAccessProvider = libraryAccessProvider;
        _ratingProvider = ratingProvider;
        _logger = logger;
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
        Guid? callerId = null;
        try
        {
            var userId = User.GetUserId();
            callerId = userId;
            interaction = await _context.UserMediaInteractions
                .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == id);
        }
        catch
        {
            // Ignore if user claim not found (shouldn't happen with [Authorize])
        }

        var dto = MediaItemDto.FromMediaItem(item, "/api/v1/image/proxy", interaction);
        await HydrateVersionsAsync(dto, item, callerId);
        await HydrateSeriesQualityAggregateAsync(dto, item);
        return dto;
    }

    /// <summary>
    /// DV-WI-022 — a SERIES has no file of its own, so its detail header used to sample
    /// an arbitrary episode for quality info (with mixed 1080p/4K copies, a coin flip).
    /// Hydrate the honest aggregate instead: best height/width across the show's live
    /// episodes, HDR if ANY episode has it, and the matching display label.
    /// </summary>
    private async Task HydrateSeriesQualityAggregateAsync(MediaItemDto dto, MediaItem item)
    {
        if (item.Type != MediaType.Series) return;

        var best = await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == item.Id && m.Type == MediaType.Episode && !m.IsMissing && m.Height != null)
            .OrderByDescending(m => m.Height)
            .ThenByDescending(m => m.HdrFormat != null && m.HdrFormat != "")
            .Select(m => new { m.Width, m.Height })
            .FirstOrDefaultAsync();
        if (best == null) return;

        dto.Width = best.Width;
        dto.Height = best.Height;
        dto.HdrFormat = await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == item.Id && m.Type == MediaType.Episode && !m.IsMissing
                     && m.HdrFormat != null && m.HdrFormat != "")
            .Select(m => m.HdrFormat)
            .FirstOrDefaultAsync();
        dto.VersionLabel = Helpers.VersionLabelHelper.ResolutionLabel(best.Height)
            + (dto.HdrFormat != null ? $" {dto.HdrFormat}" : null);
    }

    /// <summary>
    /// DV-WI-013 — detail responses list every version (file copy) of the item's group,
    /// ordered by the primary rule (plan §2.2): explicit PreferredVersion override, else
    /// max height → HDR present → max bitrate → newest → id. The primary is COMPUTED
    /// here, never stored, so it cannot drift as files come and go. List endpoints skip
    /// this on purpose (VersionCount stays 1) — no per-row sibling queries in grids.
    /// </summary>
    private async Task HydrateVersionsAsync(MediaItemDto dto, MediaItem item, Guid? userId)
    {
        if (item.VersionGroupId == null || item.Type is not (MediaType.Movie or MediaType.Episode)) return;

        var siblings = await _context.MediaItems.AsNoTracking()
            .Where(m => m.VersionGroupId == item.VersionGroupId && !m.IsMissing)
            .ToListAsync();
        if (siblings.Count <= 1) return;

        var siblingIds = siblings.Select(s => s.Id).ToList();
        var interactions = userId == null
            ? new Dictionary<Guid, UserMediaInteraction>()
            : await _context.UserMediaInteractions.AsNoTracking()
                .Where(i => i.UserId == userId && siblingIds.Contains(i.MediaItemId))
                .ToDictionaryAsync(i => i.MediaItemId);

        var ordered = Services.Media.VersionPrimaryRule.OrderPrimaryFirst(siblings).ToList();

        dto.VersionCount = ordered.Count;
        dto.Versions = ordered.Select((s, index) => new VersionDto(
            s.Id,
            Helpers.VersionLabelHelper.BuildLabel(s),
            s.Width, s.Height, s.HdrFormat, s.Bitrate, s.Container, s.Size,
            DurationSeconds: s.Duration > 0 ? s.Duration : null,
            IsPrimary: index == 0,
            Preferred: s.PreferredVersion,
            Watched: interactions.TryGetValue(s.Id, out var i) && i.IsWatched,
            PlaybackPosition: interactions.TryGetValue(s.Id, out var p) ? p.PlaybackPosition : null)).ToList();
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
        catch (Exception ex)
        {
            // B-20: degraded-but-usable is intended (recent rows render without
            // watched/progress badges), but the failure must be visible in logs —
            // a silent swallow here hid real DB errors.
            _logger.LogWarning(ex, "Failed to load user interactions for the recent-media row");
        }

        return distinctItems.Select(i => {
            interactions.TryGetValue(i.Id, out var interaction);
            return MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy", interaction);
        }).ToList();
    }

    /// <summary>
    /// R-WI-020 — personalized home rows for the calling user. Empty list (200)
    /// when the user has no play history; the client renders nothing.
    /// </summary>
    /// <param name="scope">
    /// Scope of the Most Watched row: "everyone" (default) ranks all users' plays
    /// together, "me" ranks only the caller's. Unrecognised values fall back to
    /// "everyone" rather than erroring — this drives a cosmetic row toggle, and a
    /// stale client sending a bad value should still get a usable home page. Every
    /// other row is personal regardless, and both scopes stay ACL/rating-filtered
    /// for the caller.
    /// </param>
    [HttpGet("home-rows")]
    public async Task<ActionResult<IReadOnlyList<HomeRowDto>>> GetHomeRows(
        [FromQuery] int itemsPerRow = 15, [FromQuery] string scope = "everyone")
    {
        var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId)) return Unauthorized();
        var acrossAllUsers = !string.Equals(scope, "me", StringComparison.OrdinalIgnoreCase);

        var taste = await _recommendationService.GetHomeRowsAsync(userId, itemsPerRow, acrossAllUsers);
        var catalog = await _recommendationService.GetCatalogRowsAsync(userId, itemsPerRow);

        // Taste rows first (they are the more personal signal), catalog rows after.
        // Catalog rows may legitimately repeat an item already shown above: they answer
        // a different question ("what else is in this genre" / "what have I not touched")
        // and suppressing overlap would leave them arbitrarily short.
        return Ok(taste.Concat(catalog).ToList());
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
        const string Esc = "\\";

        // Wave C — apply per-user library ACL before any other narrowing so
        // pagination/limit math operates on the user's visible set only.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        // R-WI-017 / D-12: every browse path applies the content-rating ceiling —
        // search did not, so a rating-restricted account could surface blocked
        // titles by name (and the multi-field expansion below would have widened
        // that to cast and descriptions).
        var ceilings = await _ratingProvider.GetCurrentAsync();

        var matchQuery = BuildSearchMatchQuery(searchPattern, access, ceilings);

        // Per-library top hits instead of one global Take. The old flat cap
        // (limit * 5 across the whole server) let one strong library push every
        // other library's hits past the cutoff, so those libraries VANISHED from
        // the dropdown — indistinguishable from having no matches. One bounded
        // query per matching library guarantees every library with hits shows its
        // best few; on a personal server that's a handful of small indexed
        // queries, not a fan-out.
        var matchingLibraryIds = await matchQuery
            .Select(m => m.LibraryId)
            .Distinct()
            .ToListAsync();

        var libraries = await _context.Libraries
            .AsNoTracking()
            .Where(l => matchingLibraryIds.Contains(l.Id))
            .OrderBy(l => l.Order)
            .Take(20) // defensive bound; a personal server has nowhere near 20 libraries
            .ToListAsync();

        var results = new List<GlobalSearchResultDto>();
        foreach (var library in libraries)
        {
            var items = await matchQuery
                .Where(m => m.LibraryId == library.Id)
                // Ranking within the library: title-prefix first, then any title
                // match, then other-field matches; title tiebreak keeps re-runs stable.
                .OrderBy(m => EF.Functions.Like(m.Title, $"{escaped}%", Esc) ? 0
                            : EF.Functions.Like(m.Title, searchPattern, Esc) ? 1 : 2)
                .ThenBy(m => m.Title)
                .Take(limit)
                .ToListAsync();

            if (items.Count == 0) continue; // raced a delete; skip rather than emit an empty group

            results.Add(new GlobalSearchResultDto
            {
                LibraryId = library.Id,
                LibraryName = library.Name,
                LibraryType = library.Type.ToString(),
                Items = items.Select(i => MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy")).ToList(),
                BestMatchTier = items.Min(i => TitleMatchTier(i.Title, query)),
                MatchReasons = BuildMatchReasons(items, query),
            });
        }

        await ResolveCastMatchReasonsAsync(results, query, searchPattern);

        // Group order is match quality, then the library's configured position.
        // This used to fall out of GroupBy's first-appearance order over a flat
        // item sort — the same outcome, but by accident; now it's stated.
        return Ok(results
            .OrderBy(r => r.BestMatchTier)
            .ThenBy(r => libraries.First(l => l.Id == r.LibraryId).Order)
            .ToList());
    }

    /// <summary>
    /// The search population: every item the caller may see whose fields match.
    ///
    /// R-WI-017 multi-field matching (LIKE-over-joins per the plan — FTS5 is an
    /// explicit follow-up). Field breadth is tiered by type to keep results sane:
    /// - top-level items (movies/series/albums/…): title, description, genre,
    ///   cast/person, and for albums the artist name;
    /// - tracks: title + artist/album names (genre/description would return every
    ///   track of every matching album);
    /// - episodes: title only (inherited metadata would flood results), gated on
    ///   the parent series also passing the rating ceiling so an episode row with
    ///   its own permissive rating is not a side door (review MED-LOW);
    /// - comic issues: title only (B-06 — issues inherit series metadata);
    /// - Seasons excluded: "Season 1" matches every show and carries no
    ///   information the series hit doesn't.
    /// </summary>
    private IQueryable<MediaItem> BuildSearchMatchQuery(
        string searchPattern, LibraryAccess access, UserRatingCeilings ceilings)
    {
        const string Esc = "\\";
        var ratedSeries = _context.MediaItems.ApplyContentRatingFilter(ceilings);

        return _context.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .Include(m => m.Series) // episode poster fallback + seriesTitle name context
            .Include(m => m.Artist) // track/album subtitle context (metadata.artist)
            .Include(m => m.Album)  // track subtitle context (metadata.album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m =>
                (m.Type != MediaType.Episode && m.Type != MediaType.Audio && m.Type != MediaType.Season && m.Type != MediaType.ComicIssue && (
                    EF.Functions.Like(m.Title, searchPattern, Esc)
                    || (m.Overview != null && EF.Functions.Like(m.Overview, searchPattern, Esc))
                    || m.MediaItemGenres.Any(mg => mg.Genre != null && EF.Functions.Like(mg.Genre.Name, searchPattern, Esc))
                    || m.MediaItemCasts.Any(mc => mc.Person != null && EF.Functions.Like(mc.Person.Name, searchPattern, Esc))
                    || (m.Artist != null && EF.Functions.Like(m.Artist.Title, searchPattern, Esc))))
                || (m.Type == MediaType.ComicIssue && EF.Functions.Like(m.Title, searchPattern, Esc))
                || (m.Type == MediaType.Audio && (
                    EF.Functions.Like(m.Title, searchPattern, Esc)
                    || (m.Artist != null && EF.Functions.Like(m.Artist.Title, searchPattern, Esc))
                    || (m.Album != null && EF.Functions.Like(m.Album.Title, searchPattern, Esc))))
                || (m.Type == MediaType.Episode
                    && EF.Functions.Like(m.Title, searchPattern, Esc)
                    && ratedSeries.Any(s => s.Id == m.SeriesId)))
            // DV-WI-015/016: duplicate copies of one title are ONE search result (the
            // computed primary) — two byte-identical rows in the dropdown helped no one.
            .OnePerVersionGroup(_context.MediaItems.AsNoTracking());
    }

    /// <summary>
    /// In-memory mirror of the SQL ranking cases: 0 title-prefix, 1 title-contains,
    /// 2 matched via some other field. OrdinalIgnoreCase approximates SQLite's
    /// ASCII-only LIKE case folding closely enough for a rank signal.
    /// </summary>
    private static int TitleMatchTier(string title, string query)
        => title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
         : title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1
         : 2;

    /// <summary>
    /// "Why is this here?" labels for items whose title did NOT match. Resolved
    /// from data the search query already loads (overview, genres, artist,
    /// album); cast matches — the one field that would need another join — are
    /// filled in afterwards by <see cref="ResolveCastMatchReasonsAsync"/>.
    /// </summary>
    private static Dictionary<string, string> BuildMatchReasons(List<MediaItem> items, string query)
    {
        var reasons = new Dictionary<string, string>();
        foreach (var item in items)
        {
            if (TitleMatchTier(item.Title, query) != 2) continue;

            var genre = item.MediaItemGenres
                .Select(mg => mg.Genre?.Name)
                .FirstOrDefault(n => n != null && n.Contains(query, StringComparison.OrdinalIgnoreCase));

            string? reason =
                item.Artist?.Title.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                    ? $"Matched artist: {item.Artist.Title}"
                : item.Album?.Title.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                    ? $"Matched album: {item.Album.Title}"
                : genre != null
                    ? $"Matched genre: {genre}"
                : item.Overview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                    ? "Matched description"
                : null; // cast — resolved by the follow-up query

            if (reason != null) reasons[item.Id.ToString()] = reason;
        }
        return reasons;
    }

    /// <summary>
    /// Fills in "Matched cast: X" for tier-2 items no loaded field explained.
    /// One bounded query across all groups rather than an Include on the search
    /// itself — cast lists are large and only a handful of items ever need this.
    /// </summary>
    private async Task ResolveCastMatchReasonsAsync(
        List<GlobalSearchResultDto> results, string query, string searchPattern)
    {
        const string Esc = "\\";
        var unexplained = results
            .SelectMany(r => r.Items
                .Where(i => TitleMatchTier(i.Title, query) == 2)
                .Select(i => (Group: r, i.Id)))
            .Where(x => !x.Group.MatchReasons.ContainsKey(x.Id.ToString()))
            .ToList();
        if (unexplained.Count == 0) return;

        var ids = unexplained.Select(x => x.Id).ToList();
        var castHits = await _context.MediaItemCasts
            .AsNoTracking()
            .Where(mc => ids.Contains(mc.MediaItemId)
                && mc.Person != null
                && EF.Functions.Like(mc.Person.Name, searchPattern, Esc))
            .Select(mc => new { mc.MediaItemId, mc.Person!.Name })
            .ToListAsync();

        var nameByItem = castHits
            .GroupBy(c => c.MediaItemId)
            .ToDictionary(g => g.Key, g => g.First().Name);

        foreach (var (group, id) in unexplained)
        {
            if (nameByItem.TryGetValue(id, out var name))
                group.MatchReasons[id.ToString()] = $"Matched cast: {name}";
        }
    }
}
