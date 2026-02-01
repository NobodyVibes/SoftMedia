using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Infrastructure;

public class LibraryRepository : ILibraryRepository
{
    private readonly AppDbContext _context;

    public LibraryRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Library?> GetByIdAsync(Guid id)
    {
        return await _context.Libraries.FindAsync(id);
    }

    public async Task<IEnumerable<Library>> GetAllAsync()
    {
        return await _context.Libraries.OrderBy(l => l.Order).ToListAsync();
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
        return await _context.Libraries.AnyAsync(l => l.Id == id);
    }

    public async Task<bool> IsPathUsedAsync(string path)
    {
        // For SQLite with JSON conversion, fetching and checking in memory is reliable and efficient for small sets
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

        var query = _context.MediaItems.AsNoTracking().Where(m => m.LibraryId == libraryId);

        // TV Library: Show only Series
        if (library.Type == LibraryType.TV)
        {
            query = query.Where(m => m.Type == MediaType.Series);
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
            // Simple JSON string match for SQLite
            joinedQuery = joinedQuery.Where(x => x.Media.MetadataJson != null && x.Media.MetadataJson.Contains(filter.Genre));
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
         var metadataJsons = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == libraryId && m.MetadataJson != null)
            .Select(m => m.MetadataJson)
            .ToListAsync();

        var allGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var json in metadataJsons)
        {
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json!);
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

        return allGenres.OrderBy(g => g);
    }
}
