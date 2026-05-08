using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
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

    public async Task<bool> IsPathUsedAsync(string path)
    {
        // Wave C — DELIBERATELY does NOT apply the ACL filter. This is an
        // admin-only uniqueness check called from CreateLibraryAsync /
        // UpdateLibraryAsync; the calling endpoints already require
        // [Authorize(Roles = "Admin")] and admins always have unrestricted
        // access. Filtering here would be redundant and could mask a real
        // path collision if a non-admin code path ever called it.
        //
        // For SQLite with JSON conversion, fetching and checking in memory
        // is reliable and efficient for small sets.
        var libraries = await _context.Libraries.AsNoTracking().ToListAsync();
        return libraries.Any(l => l.Paths.Contains(path));
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
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres)
                .ThenInclude(mg => mg.Genre)
            .Where(m => m.LibraryId == libraryId);

        // TV Library: Show only Series
        if (library.Type == LibraryType.TV)
        {
            query = query.Where(m => m.Type == MediaType.Series);
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
            joinedQuery = joinedQuery.Where(x => x.Media.Title.ToLower().Contains(filter.Search.ToLower()));
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

        // Sorting
        joinedQuery = filter.SortBy?.ToLower() switch
        {
            "title" => joinedQuery.OrderBy(x => x.Media.Title),
            "dateadded" => joinedQuery.OrderByDescending(x => x.Media.DateAdded),
            "year" => joinedQuery.OrderByDescending(x => x.Media.Year),
            "rating" => joinedQuery.OrderByDescending(x => x.Interaction.Rating), 
            _ => joinedQuery.OrderBy(x => x.Media.Title)
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
            .Where(mg => mg.MediaItem != null && mg.MediaItem.LibraryId == libraryId && mg.Genre != null)
            .Select(mg => mg.Genre!.Name)
            .Distinct()
            .ToListAsync();

        return genres.OrderBy(g => g);
    }
}
