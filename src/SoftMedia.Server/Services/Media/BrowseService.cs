using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Media;

public interface IBrowseService
{
    Task<PagedResult<MediaItemDto>> BrowseAsync(BrowseFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Distinct genre names across everything the caller can see, optionally narrowed to
    /// certain media types. Backs the browse page's genre picker, which has no library
    /// to scope to — /libraries/{id}/genres cannot answer "genres across the server".
    /// </summary>
    Task<IReadOnlyList<string>> GetGenresAsync(BrowseFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Cross-library filtered browse — the query behind every home row's "See more".
///
/// Security posture matches <c>LibraryRepository.GetLibraryItemsAsync</c>: the library
/// ACL and the content-rating ceiling are applied to the BASE query, before any other
/// narrowing and before the count. Pagination therefore operates on the post-filter
/// set, so a restricted user never sees an inflated total or paginates into blocked
/// items — and, unlike a per-library endpoint, there is no library id to probe with.
/// </summary>
public class BrowseService : IBrowseService
{
    private readonly AppDbContext _context;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly IUserContentRatingProvider _ratingProvider;

    public BrowseService(
        AppDbContext context,
        IUserLibraryAccessProvider libraryAccessProvider,
        IUserContentRatingProvider ratingProvider)
    {
        _context = context;
        _libraryAccessProvider = libraryAccessProvider;
        _ratingProvider = ratingProvider;
    }

    public async Task<PagedResult<MediaItemDto>> BrowseAsync(BrowseFilter filter, CancellationToken ct = default)
    {
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var types = filter.EffectiveTypes();

        var query = _context.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            // EffectiveTypes clamps to BrowsableTypes, so an explicit ?types= can only
            // ever narrow — never widen into Episode/Audio/ComicIssue child rows.
            .Where(m => types.Contains(m.Type));

        if (filter.LibraryId is Guid libraryId)
            query = query.Where(m => m.LibraryId == libraryId);

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            var genre = filter.Genre.ToLower();
            query = query.Where(m => m.MediaItemGenres
                .Any(mg => mg.Genre != null && mg.Genre.Name.ToLower() == genre));
        }

        if (filter.Decade is int decade)
        {
            var start = decade;
            var end = decade + 9;
            query = query.Where(m => m.Year != null && m.Year >= start && m.Year <= end);
        }

        if (filter.Year is int year)
            query = query.Where(m => m.Year == year);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Same match surface as the library grid (R-WI-017): title, overview, genre,
            // cast. The ACL and rating gates are already on the base query, so widening
            // the text match cannot reach blocked titles.
            var s = filter.Search.ToLower();
            query = query.Where(m =>
                m.Title.ToLower().Contains(s)
                || (m.Overview != null && m.Overview.ToLower().Contains(s))
                || m.MediaItemGenres.Any(mg => mg.Genre != null && mg.Genre.Name.ToLower().Contains(s))
                || m.MediaItemCasts.Any(mc => mc.Person != null && mc.Person.Name.ToLower().Contains(s)));
        }

        var callerId = filter.UserId ?? Guid.Empty;

        if (filter.MinRating is int minRating)
        {
            query = query.Where(m => _context.UserMediaInteractions
                .Any(i => i.UserId == callerId && i.MediaItemId == m.Id && i.Rating >= minRating));
        }

        if (filter.IsFavorite is bool favorite)
        {
            query = favorite
                ? query.Where(m => _context.UserMediaInteractions
                    .Any(i => i.UserId == callerId && i.MediaItemId == m.Id && i.IsFavorite))
                : query.Where(m => !_context.UserMediaInteractions
                    .Any(i => i.UserId == callerId && i.MediaItemId == m.Id && i.IsFavorite));
        }

        if (filter.Watched is bool watched)
        {
            query = watched
                ? query.Where(m => _context.UserMediaInteractions
                    .Any(i => i.UserId == callerId && i.MediaItemId == m.Id && i.IsWatched))
                : query.Where(m => !_context.UserMediaInteractions
                    .Any(i => i.UserId == callerId && i.MediaItemId == m.Id && i.IsWatched));
        }

        if (filter.InProgress == true)
        {
            // Mirrors the SQL half of ContinueWatchingService: a Movie is in progress
            // when started and not flagged watched; a Series when any episode has been
            // started or finished (the show itself is never played directly).
            //
            // NOT identical to that row. ContinueWatchingService additionally applies the
            // credits/95% completion rule IN CODE, because it compares each row's
            // CreditsStart against its own Duration and cannot be expressed in SQL. So
            // this grid can include an item sitting past that threshold which the row
            // already dropped. Reproducing it here would mean post-filtering the page,
            // which would make TotalCount and the paging maths lie — a worse trade.
            query = query.Where(m =>
                (m.Type == MediaType.Movie && _context.UserMediaInteractions.Any(i =>
                    i.UserId == callerId && i.MediaItemId == m.Id
                    && !i.IsWatched && i.PlaybackPosition != null && i.PlaybackPosition > 0))
                || (m.Type == MediaType.Series && _context.MediaItems.Any(e =>
                    e.SeriesId == m.Id && !e.IsMissing && _context.UserMediaInteractions.Any(i =>
                        i.UserId == callerId && i.MediaItemId == e.Id
                        && (i.IsWatched || (i.PlaybackPosition != null && i.PlaybackPosition > 0))))));
        }

        if (filter.Unplayed == true)
        {
            var userId = filter.UserId ?? Guid.Empty;
            // "Never played" has to account for roll-up: a Series is unplayed only when
            // none of its EPISODES were played, and an Album only when none of its
            // TRACKS were. Testing the parent row alone would report every series as
            // unplayed, since plays are always recorded against the child.
            query = query.Where(m => !_context.PlaybackHistory.Any(h =>
                h.UserId == userId
                && (h.MediaItemId == m.Id
                    || (h.MediaItem != null && h.MediaItem.SeriesId == m.Id)
                    || (h.MediaItem != null && h.MediaItem.AlbumId == m.Id))));
        }

        // Direction is user-controllable, but omitting it must behave exactly as before
        // — every "See more" link already shipped leaves it off. See SortDirection.
        var desc = Helpers.SortDirection.IsDescending(filter.SortBy, filter.SortDir);

        // Each key is built ONCE as an expression and handed to whichever direction is
        // wanted. Writing both orderings inline per key would double the query logic and
        // invite the two copies to drift — and these keys are the fiddly ones (a Series
        // has no plays of its own; its children's must be summed).
        //
        // Expression<> rather than a local method: EF must translate these to SQL, and
        // it cannot see inside a C# method call.
        Expression<Func<MediaItem, int?>> ratingKey = m => _context.UserMediaInteractions
            .Where(i => i.UserId == callerId && i.MediaItemId == m.Id)
            .Select(i => (int?)i.Rating)
            .FirstOrDefault();

        // Plays land on Movies and EPISODES, so a Series row has none of its own and
        // must sum its children's — mirroring LibraryRepository's TV rollup.
        Expression<Func<MediaItem, int>> playCountKey = m => m.Type == MediaType.Series
            ? _context.MediaItems.Where(e => e.SeriesId == m.Id && !e.IsMissing).Sum(e => (int?)e.PlayCount) ?? 0
            : m.PlayCount;

        // The caller's OWN plays, matching the Most Watched row's Me scope.
        Expression<Func<MediaItem, int>> myPlayCountKey = m => _context.PlaybackHistory.Count(h =>
            h.UserId == callerId
            && (h.MediaItemId == m.Id
                || (h.MediaItem != null && h.MediaItem.SeriesId == m.Id)));

        Expression<Func<MediaItem, DateTime?>> lastPlayedKey = m => m.Type == MediaType.Series
            ? _context.MediaItems.Where(e => e.SeriesId == m.Id && !e.IsMissing).Max(e => (DateTime?)e.LastPlayed)
            : m.LastPlayed;

        // Ordered by the chosen key, then ALWAYS by title A-Z. The tiebreaker stays
        // ascending even when the primary key is reversed — flipping "most played"
        // should not also scramble the names of everything tied on zero plays.
        // NULL dates/years sort last under DESC in SQLite, so undated items trail
        // rather than heading the list.
        IOrderedQueryable<MediaItem> ordered = filter.SortBy?.ToLower() switch
        {
            "dateadded" => desc ? query.OrderByDescending(m => m.DateAdded) : query.OrderBy(m => m.DateAdded),
            "year" => desc ? query.OrderByDescending(m => m.Year) : query.OrderBy(m => m.Year),
            "rating" => desc ? query.OrderByDescending(ratingKey) : query.OrderBy(ratingKey),
            "playcount" => desc ? query.OrderByDescending(playCountKey) : query.OrderBy(playCountKey),
            "myplaycount" => desc ? query.OrderByDescending(myPlayCountKey) : query.OrderBy(myPlayCountKey),
            "lastplayed" => desc ? query.OrderByDescending(lastPlayedKey) : query.OrderBy(lastPlayedKey),
            _ => desc ? query.OrderByDescending(m => m.Title) : query.OrderBy(m => m.Title),
        };
        query = ordered.ThenBy(m => m.Title);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        return new PagedResult<MediaItemDto>
        {
            Items = items
                .Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy))
                .ToList(),
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
        };
    }

    public async Task<IReadOnlyList<string>> GetGenresAsync(BrowseFilter filter, CancellationToken ct = default)
    {
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var types = filter.EffectiveTypes();

        // Derived from VISIBLE items, not from the Genres table: listing every genre
        // would tell a restricted user which genres exist in libraries they cannot see,
        // and would offer picker options that return an empty grid.
        return await _context.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .Where(m => types.Contains(m.Type))
            .SelectMany(m => m.MediaItemGenres)
            .Where(mg => mg.Genre != null)
            .Select(mg => mg.Genre!.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);
    }
}
