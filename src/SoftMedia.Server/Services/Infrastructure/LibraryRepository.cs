using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Infrastructure;

public class LibraryRepository : ILibraryRepository
{
    private readonly AppDbContext _context;
    private readonly IUserContentRatingProvider _ratingProvider;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;

    public LibraryRepository(
        AppDbContext context,
        IUserContentRatingProvider ratingProvider,
        IUserLibraryAccessProvider libraryAccessProvider)
    {
        _context = context;
        _ratingProvider = ratingProvider;
        _libraryAccessProvider = libraryAccessProvider;
    }

    public async Task<Library?> GetByIdAsync(Guid id)
    {
        // Wave C — apply per-user ACL. Returning null for a blocked library
        // (rather than 403 upstream) lines up with SDD §6.2's "404 over 403"
        // anti-probe rule.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.Libraries
            .ApplyLibraryAccessFilter(access)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    public async Task<IEnumerable<Library>> GetAllAsync()
    {
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.Libraries
            .ApplyLibraryAccessFilter(access)
            .OrderBy(l => l.Order)
            .ToListAsync();
    }

    public async Task AddAsync(Library library)
    {
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Library library)
    {
        _context.Libraries.Update(library);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateRangeAsync(IEnumerable<Library> libraries)
    {
        _context.Libraries.UpdateRange(libraries);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Library library)
    {
        _context.Libraries.Remove(library);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        // Wave C — match GetByIdAsync semantics so existence checks respect ACL.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.Libraries
            .ApplyLibraryAccessFilter(access)
            .AnyAsync(l => l.Id == id);
    }

    public async Task<PagedResult<(MediaItem Media, UserMediaInteraction? Interaction)>> GetLibraryItemsAsync(Guid libraryId, LibraryItemFilter filter)
    {
        var library = await _context.Libraries.FindAsync(libraryId);
        if (library == null)
        {
            return new PagedResult<(MediaItem Media, UserMediaInteraction? Interaction)>
            {
                Items = new List<(MediaItem Media, UserMediaInteraction? Interaction)>(),
                TotalCount = 0
            };
        }

        // Wave C — per-user ACL gate. If the library is not in the user's
        // allow-list, return an empty page so the count number doesn't leak
        // existence (mirrors the 404-over-403 anti-probe rule). Done before
        // any other narrowing, so pagination math stays correct.
        var libraryAccess = await _libraryAccessProvider.GetCurrentAsync();
        if (!libraryAccess.IsUnrestricted && !libraryAccess.AllowedLibraryIds.Contains(libraryId))
        {
            return new PagedResult<(MediaItem Media, UserMediaInteraction? Interaction)>
            {
                Items = new List<(MediaItem Media, UserMediaInteraction? Interaction)>(),
                TotalCount = 0
            };
        }

        // Apply parental-control gate BEFORE the type-specific narrowing below.
        // Counts and pagination then operate on the post-filter set, so a child
        // user sees a smaller item count and never paginates past blocked items.
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var query = _context.MediaItems
            .AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres)
                .ThenInclude(mg => mg.Genre)
            .Where(m => m.LibraryId == libraryId)
            // DV-WI-015: duplicate copies of one title collapse to the computed primary —
            // one card per movie/episode in grids and per-library search. Applied before
            // count/sort/page so pagination math stays exact. Ungrouped rows (and every
            // container type — their group id is always null) pass through untouched.
            .OnePerVersionGroup(_context.MediaItems.AsNoTracking());

        // TV Library: Show only Series
        if (library.Type == LibraryType.TV)
        {
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                // B-05: a searched TV grid also surfaces matching EPISODES —
                // narrowing to Series first made episode titles unfindable in
                // per-library search. Episodes qualify on TITLE only (same rule
                // as global search): matching their overview/genre would flood
                // results with inherited series text.
                var episodeSearch = filter.Search.ToLower();
                query = query.Where(m => m.Type == MediaType.Series
                    || (m.Type == MediaType.Episode && m.Title.ToLower().Contains(episodeSearch)));
            }
            else
            {
                query = query.Where(m => m.Type == MediaType.Series);
            }
        }
        // Book Library: Hide comic issues — they're reached via their ComicSeries parent
        else if (library.Type == LibraryType.Book)
        {
            query = query.Where(m => m.Type != MediaType.ComicIssue);
        }
        // Music Library: Handle View Modes
        else if (library.Type == LibraryType.Music)
        {
            if (filter.ViewMode == "artists")
            {
                query = query.Where(m => m.Type == MediaType.Artist);
            }
            else if (filter.ViewMode == "albums")
            {
                query = query.Where(m => m.Type == MediaType.Album);
            }
            else 
            {
                // Default logic
                if (filter.ViewMode == "songs")
                {
                    query = query.Where(m => m.Type == MediaType.Audio);
                }
                else 
                {
                    query = query.Where(m => m.Type == MediaType.Album);
                }
            }
        }

        // Join with User Interactions
        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = (filter.UserId ?? Guid.Empty) } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        // Filtering
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // R-WI-017 multi-field: title, description, genre, cast, and (for music)
            // the artist/album names. LIKE-over-joins per the plan; the rating ceiling
            // and library ACL were already applied to the base query above, so the
            // wider match surface cannot leak blocked titles.
            var s = filter.Search.ToLower();
            joinedQuery = joinedQuery.Where(x =>
                x.Media.Title.ToLower().Contains(s)
                || (x.Media.Overview != null && x.Media.Overview.ToLower().Contains(s))
                || x.Media.MediaItemGenres.Any(mg => mg.Genre != null && mg.Genre.Name.ToLower().Contains(s))
                || x.Media.MediaItemCasts.Any(mc => mc.Person != null && mc.Person.Name.ToLower().Contains(s))
                || (x.Media.Artist != null && x.Media.Artist.Title.ToLower().Contains(s))
                || (x.Media.Album != null && x.Media.Album.Title.ToLower().Contains(s)));
        }

        if (filter.Year.HasValue)
        {
            joinedQuery = joinedQuery.Where(x => x.Media.Year == filter.Year.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            joinedQuery = joinedQuery.Where(x => x.Media.MediaItemGenres.Any(mg => mg.Genre != null && mg.Genre.Name.ToLower() == filter.Genre.ToLower()));
        }

        if (filter.MinRating.HasValue)
        {
            joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.Rating >= filter.MinRating.Value);
        }

        if (filter.IsFavorite.HasValue)
        {
            if (filter.IsFavorite.Value)
            {
                joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.IsFavorite == true);
            }
            else
            {
                joinedQuery = joinedQuery.Where(x => x.Interaction == null || x.Interaction.IsFavorite == false);
            }
        }

        if (filter.Watched.HasValue)
        {
            if (filter.Watched.Value)
            {
                joinedQuery = joinedQuery.Where(x => x.Interaction != null && x.Interaction.IsWatched == true);
            }
            else
            {
                joinedQuery = joinedQuery.Where(x => x.Interaction == null || x.Interaction.IsWatched == false);
            }
        }

        // Sorting. Direction is user-controllable; omitting it keeps each key's natural
        // direction, so existing callers and saved URLs behave exactly as before.
        var sortDesc = Helpers.SortDirection.IsDescending(filter.SortBy, filter.SortDir);

        // joinedQuery projects to an ANONYMOUS type, which cannot be named in an
        // Expression<Func<...>> declaration. This generic local function sidesteps that:
        // T is inferred from the argument, so each key is written once and the direction
        // is chosen at the call site instead of duplicating every ordering.
        static IOrderedQueryable<T> Order<T, TKey>(
            IQueryable<T> source,
            System.Linq.Expressions.Expression<Func<T, TKey>> key,
            bool descending)
            => descending ? source.OrderByDescending(key) : source.OrderBy(key);

        // R-WI-013 aggregates put to work: PlayCount/LastPlayed are maintained by the
        // play-history flow (and recomputed on history clears). All-user aggregates by
        // design. Plays land on MOVIES and EPISODES — a TV grid shows SERIES rows, so
        // its sort aggregates the episodes' counts up to the series (correlated
        // subquery; empty → 0/null). SQLite sorts NULLs last under DESC, so never-played
        // items trail either way.
        var isTv = library.Type == LibraryType.TV;

        joinedQuery = filter.SortBy?.ToLower() switch
        {
            "dateadded" => Order(joinedQuery, x => x.Media.DateAdded, sortDesc),
            "year" => Order(joinedQuery, x => x.Media.Year, sortDesc),
            "rating" => Order(joinedQuery, x => x.Interaction.Rating, sortDesc),
            "playcount" => isTv
                ? Order(joinedQuery, x => _context.MediaItems
                    .Where(e => e.SeriesId == x.Media.Id && !e.IsMissing).Sum(e => (int?)e.PlayCount) ?? 0, sortDesc)
                : Order(joinedQuery, x => x.Media.PlayCount, sortDesc),
            "lastplayed" => isTv
                ? Order(joinedQuery, x => _context.MediaItems
                    .Where(e => e.SeriesId == x.Media.Id && !e.IsMissing).Max(e => (DateTime?)e.LastPlayed), sortDesc)
                : Order(joinedQuery, x => x.Media.LastPlayed, sortDesc),
            _ => Order(joinedQuery, x => x.Media.Title, sortDesc),
        };

        var totalCount = await joinedQuery.CountAsync();
        var items = await joinedQuery
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();
            
        // Map anonymous type to Tuple (Entity, Interaction)
        var resultItems = items.Select(x => (x.Media, (UserMediaInteraction?)x.Interaction)).ToList();

        return new PagedResult<(MediaItem Media, UserMediaInteraction? Interaction)>
        {
            Items = resultItems,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize
        };
    }

    public async Task<IEnumerable<string>> GetLibraryGenresAsync(Guid libraryId)
    {
        // Wave C — refuse to enumerate genres for a library the user can't see.
        // Returning an empty list (rather than 404 here — controllers translate)
        // matches the empty-page behaviour of GetLibraryItemsAsync.
        var libraryAccess = await _libraryAccessProvider.GetCurrentAsync();
        if (!libraryAccess.IsUnrestricted && !libraryAccess.AllowedLibraryIds.Contains(libraryId))
        {
            return Enumerable.Empty<string>();
        }

        var genres = await _context.MediaItemGenres
            .AsNoTracking()
            .Where(mg => mg.MediaItem != null && mg.MediaItem.LibraryId == libraryId && !mg.MediaItem.IsMissing && mg.Genre != null)
            .Select(mg => mg.Genre!.Name)
            .Distinct()
            .ToListAsync();

        return genres.OrderBy(g => g);
    }
}
