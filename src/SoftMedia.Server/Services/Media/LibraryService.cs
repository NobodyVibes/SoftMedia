using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Constants;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace SoftMedia.Server.Services.Media;

public class LibraryService : ILibraryService
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ILibraryScanQueueService _scanQueueService;
    private readonly IImageCacheService _imageCacheService;
    private readonly LibraryWatcher _libraryWatcher;
    private readonly AppDbContext _context; // Direct access for cache management
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly IUserContentRatingProvider _ratings;
    private readonly ILogger<LibraryService> _logger;

    public LibraryService(
        ILibraryRepository libraryRepository,
        IMediaRepository mediaRepository,
        ILibraryScanQueueService scanQueueService,
        IImageCacheService imageCacheService,
        LibraryWatcher libraryWatcher,
        AppDbContext context,
        IUserLibraryAccessProvider libraryAccess,
        IUserContentRatingProvider ratings,
        ILogger<LibraryService> logger)
    {
        _libraryRepository = libraryRepository;
        _mediaRepository = mediaRepository;
        _scanQueueService = scanQueueService;
        _imageCacheService = imageCacheService;
        _libraryWatcher = libraryWatcher;
        _context = context;
        _libraryAccess = libraryAccess;
        _ratings = ratings;
        _logger = logger;
    }

    public async Task<Library?> GetLibraryAsync(Guid id)
    {
        return await _libraryRepository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Library>> GetLibrariesAsync()
    {
        return await _libraryRepository.GetAllAsync();
    }

    public async Task<Library> CreateLibraryAsync(CreateLibraryRequest request)
    {
        var canonicalPaths = CanonicaliseAll(request.Paths);
        foreach (var path in canonicalPaths)
        {
            if (!Directory.Exists(path))
            {
                throw new ArgumentException($"Directory does not exist: {path}");
            }
            await ThrowIfPathCollidesAsync(path, excludeLibraryId: null);
        }

        // Detect within-request duplicates (e.g. admin entered the same folder twice
        // with different casing or separator style) — the DB duplicate check won't
        // catch these because none of the rows have been saved yet.
        RejectIntraRequestDuplicates(canonicalPaths);

        var libraries = await _libraryRepository.GetAllAsync();
        var library = new Library
        {
            Name = request.Name,
            Type = request.Type,
            Paths = canonicalPaths,
            Order = libraries.Count()
        };

        await _libraryRepository.AddAsync(library);
        _scanQueueService.EnqueueScan(library.Id, library.Name);

        // R-WI-007: register real-time watchers for the new library now, rather than only
        // at the next server restart. Best-effort — the library is already persisted and a
        // scan enqueued, so a transient failure here (e.g. a momentary SQLite lock while the
        // watcher re-reads libraries) must not fail the create; the next refresh/restart or
        // a scheduled scan still covers it.
        await RefreshWatchersSafeAsync(library.Id);

        return library;
    }

    /// Invokes the watcher refresh without letting its failure surface as a 500 on an
    /// otherwise-successful create/edit (diff-review MEDIUM). No-ops when the watcher loop
    /// isn't running.
    private async Task RefreshWatchersSafeAsync(Guid libraryId)
    {
        try
        {
            await _libraryWatcher.RefreshWatchersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "File-watcher refresh failed after change to library {LibraryId}; real-time " +
                "watching may lag until the next successful refresh, scheduled scan, or restart.",
                libraryId);
        }
    }

    public async Task UpdateLibraryAsync(Guid id, UpdateLibraryRequest request)
    {
        var library = await _libraryRepository.GetByIdAsync(id);
        if (library == null) throw new KeyNotFoundException("Library not found");

        var canonicalPaths = CanonicaliseAll(request.Paths);
        foreach (var path in canonicalPaths)
        {
            if (!Directory.Exists(path))
            {
                throw new ArgumentException($"Directory does not exist: {path}");
            }
            await ThrowIfPathCollidesAsync(path, excludeLibraryId: id);
        }

        RejectIntraRequestDuplicates(canonicalPaths);

        library.Name = request.Name;
        library.Type = request.Type;
        library.Paths = canonicalPaths;

        await _libraryRepository.UpdateAsync(library);

        // R-WI-007: rebuild watchers so newly-added paths are watched and watchers on
        // removed paths are torn down (and their stale pending files pruned). Best-effort —
        // a refresh failure must not fail the persisted edit.
        await RefreshWatchersSafeAsync(library.Id);
    }

    // --- Path safety helpers (Todo 08) -------------------------------------

    /// <summary>
    /// Throws when <paramref name="canonicalPath"/> is already claimed by a library
    /// other than <paramref name="excludeLibraryId"/> — naming the owner, because
    /// "already used by another library" sent an admin hunting through every library's
    /// paths to find which one (a deleted-then-recreated test library, in the incident
    /// that motivated this).
    ///
    /// BOTH sides are canonicalised before comparing, with Windows-appropriate case
    /// handling (PathsEqual). Create used to do a raw case-sensitive Contains against
    /// the stored strings while update canonicalised — so a legacy row whose stored
    /// form differed (casing, separators, trailing slash) let a second library claim
    /// the same folder through create, and the two then double-scanned it.
    ///
    /// Queries Libraries UNFILTERED (no ACL): this is an integrity check on admin-only
    /// endpoints, and filtering could hide a real collision with a row the caller's
    /// ACL happens to exclude.
    /// </summary>
    private async Task ThrowIfPathCollidesAsync(string canonicalPath, Guid? excludeLibraryId)
    {
        var libraries = await _context.Libraries.AsNoTracking().ToListAsync();

        var owner = libraries
            .Where(l => excludeLibraryId == null || l.Id != excludeLibraryId.Value)
            .FirstOrDefault(l => (l.Paths ?? new List<string>()).Any(existing =>
            {
                // A legacy row holding an unparseable path must not brick every
                // create/update on the server — fall back to comparing it raw.
                string existingCanonical;
                try { existingCanonical = Canonicalise(existing); }
                catch (ArgumentException) { existingCanonical = existing; }
                return PathsEqual(existingCanonical, canonicalPath);
            }));

        if (owner != null)
        {
            throw new ArgumentException(
                $"Path '{canonicalPath}' is already used by library '{owner.Name}'.");
        }
    }

    /// Canonicalise a single path: trim whitespace, resolve to absolute via
    /// `Path.GetFullPath` (which handles `.` `..` relative segments), and strip
    /// any trailing separator so two logical forms produce identical strings.
    /// Throws <see cref="ArgumentException"/> on an empty input or a value
    /// `Path.GetFullPath` cannot interpret (e.g. bad drive letters on Windows).
    private static string Canonicalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Library path cannot be empty.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Library path '{path}' is not a valid filesystem path.", ex);
        }

        // Strip a trailing directory separator unless the path IS a root drive
        // (e.g. `C:\` on Windows where the root itself requires the separator).
        if (resolved.Length > 1 &&
            (resolved.EndsWith(Path.DirectorySeparatorChar) || resolved.EndsWith(Path.AltDirectorySeparatorChar)) &&
            !(resolved.Length == 3 && resolved[1] == ':'))
        {
            resolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return resolved;
    }

    private static List<string> CanonicaliseAll(IEnumerable<string> paths)
    {
        return paths.Select(Canonicalise).ToList();
    }

    private static bool PathsEqual(string a, string b)
    {
        // Windows is case-insensitive; Linux/macOS case-sensitive.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }

    private static void RejectIntraRequestDuplicates(IReadOnlyList<string> canonicalPaths)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var p in canonicalPaths)
        {
            if (!seen.Add(p))
            {
                throw new ArgumentException($"Duplicate path in request (after canonicalisation): {p}");
            }
        }
    }

    public async Task<bool> DeleteLibraryAsync(Guid id)
    {
        // GetByIdAsync is ACL-filtered; null covers both "no such library" and
        // "caller cannot see it". Report false either way so the endpoint answers
        // 404 instead of a silent 204 that claims a delete which never happened —
        // the anti-probe rule (404 over 403) keeps the two cases indistinguishable.
        var library = await _libraryRepository.GetByIdAsync(id);
        if (library == null) return false;

        _libraryWatcher.RemoveWatchersForLibrary(id);

        var mediaToCleanup = await _mediaRepository.GetMediaIdsAndTypesByLibraryAsync(id);
        _imageCacheService.DeleteImagesForLibrary(mediaToCleanup.Select(m => (m.Id, m.Type)));

        // Clean up cast images for TV libraries using the relational MediaItemCasts table
        if (library.Type == LibraryType.TV)
        {
            var castPersonIds = await ExtractCastPersonIdsForLibraryAsync(id);
            if (castPersonIds.Count > 0)
            {
                _imageCacheService.DeleteCastImagesForPersonIds(castPersonIds);
                _logger.LogInformation("Deleted {Count} cast images for library {Name}", castPersonIds.Count, library.Name);
            }
        }

        await _libraryRepository.DeleteAsync(library);

        // Invalidate Hero cache to remove any items from the deleted library
        var heroCache = await _context.HeroCaches.FindAsync(1);
        if (heroCache != null)
        {
            _context.HeroCaches.Remove(heroCache);
            await _context.SaveChangesAsync();
            _logger.LogDebug("Invalidated hero cache after deleting library {LibraryName}", library.Name);
        }

        return true;
    }

    public async Task ReorderLibrariesAsync(List<Guid> orderedIds)
    {
        var libraries = (await _libraryRepository.GetAllAsync()).ToList();
        
        foreach (var library in libraries)
        {
            var index = orderedIds.IndexOf(library.Id);
            if (index != -1)
            {
                library.Order = index;
            }
        }

        await _libraryRepository.UpdateRangeAsync(libraries);
    }

    /// <summary>
    /// Extracts all cast person IDs from the relational MediaItemCasts table for a library.
    /// Used during library deletion to clean up cast image files.
    /// </summary>
    private async Task<List<int>> ExtractCastPersonIdsForLibraryAsync(Guid libraryId)
    {
        // EF fills `MediaItem` via the FK on MediaItemCast; nullable-flow
        // analysis doesn't know that, hence the `!`.
        var personIds = await _context.MediaItemCasts
            .AsNoTracking()
            .Where(c => c.MediaItem!.LibraryId == libraryId)
            .Select(c => c.PersonId)
            .Distinct()
            .ToListAsync();

        return personIds;
    }

    public async Task<IEnumerable<string>> GetLibraryGenresAsync(Guid libraryId)
    {
        return await _libraryRepository.GetLibraryGenresAsync(libraryId);
    }

    public async Task<PagedResult<MediaItemDto>> GetLibraryItemsAsync(Guid libraryId, LibraryItemFilter filter)
    {
        var result = await _libraryRepository.GetLibraryItemsAsync(libraryId, filter);

        var dtos = result.Items.Select(x => MapToDto(x.Media, x.Interaction)).ToList();

        return new PagedResult<MediaItemDto>
        {
            Items = dtos,
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize
        };
    }

    public async Task<IEnumerable<MediaItemDto>> GetSeriesEpisodesAsync(Guid seriesId, Guid userId)
    {
        var items = await _mediaRepository.GetSeriesEpisodesWithInteractionsAsync(seriesId, userId);
        return items.Select(x => MapToDto(x.Media, x.Interaction)).ToList();
    }

    public async Task<IEnumerable<MediaItemDto>> GetComicIssuesAsync(Guid seriesId, Guid userId)
    {
        var items = await _mediaRepository.GetComicIssuesWithInteractionsAsync(seriesId, userId);
        return items.Select(x => MapToDto(x.Media, x.Interaction)).ToList();
    }

    public async Task<IEnumerable<MediaItemDto>> GetArtistAlbumsAsync(Guid artistId, Guid userId)
    {
        var items = await _mediaRepository.GetArtistAlbumsWithInteractionsAsync(artistId, userId);
        return items.Select(x => MapToDto(x.Media, x.Interaction)).ToList();
    }

    public async Task<IEnumerable<MediaItemDto>> GetAlbumTracksAsync(Guid albumId, Guid userId)
    {
        var items = await _mediaRepository.GetAlbumTracksWithInteractionsAsync(albumId, userId);
        return items.Select(x => MapToDto(x.Media, x.Interaction)).ToList();
    }

    public async Task<LibraryScanJob> ScanLibraryAsync(Guid id)
    {
        var library = await _libraryRepository.GetByIdAsync(id);
        if (library == null) throw new KeyNotFoundException("Library not found");

        if (_scanQueueService.IsLibraryInQueue(id))
        {
            var existingJob = _scanQueueService.GetAllJobs()
                .FirstOrDefault(j => j.LibraryId == id && 
                    (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));
            
            if (existingJob != null) return existingJob;
        }

        return _scanQueueService.EnqueueScan(id, library.Name);
    }

    public IEnumerable<LibraryScanJob> GetScanQueue() => _scanQueueService.GetAllJobs();

    public LibraryScanJob? GetScanJobStatus(Guid jobId) => _scanQueueService.GetJobStatus(jobId);

    public async Task<IEnumerable<object>> GetSeriesSeasonsAsync(Guid seriesId)
    {
        // Fetch Season entities
        var seasons = (await _mediaRepository.GetSeriesSeasonsAsync(seriesId)).ToList();

        if (seasons.Count == 0)
        {
            // Fallback for legacy/non-migrated data: Use distinct query
            var seasonNumbers = await _mediaRepository.GetDistinctSeasonNumbersAsync(seriesId);

            if (seasonNumbers.Count > 0)
            {
                var resultList = new List<object>();
                foreach (var num in seasonNumbers)
                {
                    var count = await _mediaRepository.GetEpisodeCountAsync(seriesId, num);
                    resultList.Add(new { 
                        number = num, 
                        poster = (string?)null, 
                        episodeCount = count,
                        premiereDate = (string?)null
                    });
                }
                return resultList;
            }
        }

        var result = new List<object>();

        foreach (var season in seasons)
        {
            // Use promoted PosterUrl column on Season entity
            string? poster = null;
            if (!string.IsNullOrEmpty(season.PosterUrl))
            {
                poster = season.PosterUrl;
                if (poster.StartsWith("http"))
                {
                    poster = $"{MediaConstants.Routes.ImageProxy}?url={Uri.EscapeDataString(poster)}";
                }
            }

            // Use promoted ReleaseDate column for premiere date
            var premiereDate = season.ReleaseDate?.ToString("yyyy-MM-dd");

            // Real-time episode count
            var realCount = await _mediaRepository.GetEpisodeCountAsync(seriesId, season.SeasonNumber ?? 0);
            
            result.Add(new
            {
                id = season.Id,
                number = season.SeasonNumber,
                poster = poster,
                episodeCount = realCount,
                premiereDate = premiereDate,
                overview = season.Overview
            });
        }

        return result;
    }

    private MediaItemDto MapToDto(MediaItem m, UserMediaInteraction? interaction)
    {
        var dto = MediaItemDto.FromMediaItem(m, MediaConstants.Routes.ImageProxy);
        if (interaction != null)
        {
            dto.PersonalRating = interaction.Rating; // Individual user rating (Yellow Star)
            dto.IsFavorite = interaction.IsFavorite;
            dto.Watched = interaction.IsWatched;
            dto.PlaybackPosition = interaction.PlaybackPosition;
            
            if (interaction.PlaybackPosition > 0 && m.Duration > 0)
            {
                dto.Progress = (interaction.PlaybackPosition / (double)m.Duration) * 100;
            }
        }
        
        // Ensure UserRating (SoftMedia Average / Violet Star) is ALWAYS set from item.InternalRating
        // MediaItemDto.FromMediaItem already does this, but we reinforce it here
        dto.UserRating = m.InternalRating;
        
        return dto;
    }

    public async Task UpdateRecentlyAddedCacheAsync(Guid libraryId)
    {
        _logger.LogInformation("Updating Recently Added cache for Library {LibraryId}", libraryId);

        // Fetch recent items (top 20)
        var filter = new LibraryItemFilter
        {
            Page = 1,
            PageSize = 20,
            SortBy = "DateAdded_Desc",
            UserId = Guid.Empty // Admin/System view for cache (no user specific interactions)
        };

        var result = await _libraryRepository.GetLibraryItemsAsync(libraryId, filter);
        var dtos = result.Items.Select(x => MapToDto(x.Media, null)).ToList(); // No interaction data in cache

        var json = JsonSerializer.Serialize(dtos);

        var cache = await _context.LibraryRecentCaches.FindAsync(libraryId);
        if (cache == null)
        {
            cache = new LibraryRecentCache
            {
                LibraryId = libraryId,
                CachedJson = json,
                LastUpdated = DateTime.UtcNow
            };
            _context.LibraryRecentCaches.Add(cache);
        }
        else
        {
            cache.CachedJson = json;
            cache.LastUpdated = DateTime.UtcNow;
            _context.Entry(cache).State = EntityState.Modified;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Recently Added cache updated for Library {LibraryId}", libraryId);
    }

    public async Task<IEnumerable<MediaItemDto>> GetRecentlyAddedAsync(Guid libraryId, Guid userId)
    {
        // Audit wave-2 H-1: the recently-added cache is built with an UNFILTERED system view
        // (UpdateRecentlyAddedCacheAsync uses UserId=Guid.Empty), so it must be gated per-caller
        // before return — both the per-library ACL and the content-rating ceiling — matching the
        // combined gate MediaRepository applies everywhere else. Without this, a denied/rating-
        // restricted user reads cross-library metadata (and previously the on-disk path).
        var access = await _libraryAccess.GetCurrentAsync();
        if (!access.IsUnrestricted && !access.AllowedLibraryIds.Contains(libraryId))
        {
            return Enumerable.Empty<MediaItemDto>();
        }

        var cache = await _context.LibraryRecentCaches.AsNoTracking().FirstOrDefaultAsync(c => c.LibraryId == libraryId);
        
        List<MediaItemDto>? items = null;
        if (cache != null)
        {
            try 
            {
                items = JsonSerializer.Deserialize<List<MediaItemDto>>(cache.CachedJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize recently added cache for {LibraryId}", libraryId);
            }
        }

        if (items == null)
        {
            // Fallback or first run
            await UpdateRecentlyAddedCacheAsync(libraryId);
            
            var result = await _libraryRepository.GetLibraryItemsAsync(libraryId, new LibraryItemFilter
            {
                Page = 1,
                PageSize = 20,
                SortBy = "DateAdded_Desc",
                UserId = Guid.Empty
            });
            items = result.Items.Select(x => MapToDto(x.Media, null)).ToList();
        }

        // Audit wave-2 H-1: re-apply the content-rating ceiling per caller — the shared cache is
        // rating-blind. Reuse the canonical EF predicate via a cheap PK lookup over the <=20 ids so
        // a rating-restricted (but library-allowed) child never sees over-rating titles.
        var ceilings = await _ratings.GetCurrentAsync();
        if (!ceilings.IsUnrestricted && items != null && items.Count > 0)
        {
            var ratingIds = items.Select(i => i.Id).ToList();
            var allowedIds = (await _context.MediaItems.AsNoTracking()
                .Where(m => ratingIds.Contains(m.Id))
                .ApplyContentRatingFilter(ceilings)
                .Select(m => m.Id)
                .ToListAsync()).ToHashSet();
            items = items.Where(i => allowedIds.Contains(i.Id)).ToList();
        }

        // Inject personalized interactions and live ratings if we have items
    if (items is { Count: > 0 })
    {
        var itemIds = items.Select(i => i.Id).ToList();
        
        // Fetch current average ratings and user-specific interactions in parallel if possible
        // but for simplicity we'll just do another query or combine if refactoring.
        // For now, let's just combine the logic here.
        
        var liveData = await _context.MediaItems
            .AsNoTracking()
            .Where(m => itemIds.Contains(m.Id))
            .Select(m => new { m.Id, m.InternalRating })
            .ToDictionaryAsync(x => x.Id, x => x.InternalRating);

        List<UserMediaInteraction>? interactions = null;
        if (userId != Guid.Empty)
        {
            interactions = await _context.UserMediaInteractions
                .Where(ui => ui.UserId == userId && itemIds.Contains(ui.MediaItemId))
                .ToListAsync();
        }

        var interactionMap = interactions?.ToDictionary(i => i.MediaItemId);
        
        foreach (var item in items)
        {
            // Re-hydrate live average rating
            if (liveData.TryGetValue(item.Id, out var rating))
            {
                item.UserRating = rating;
            }

            // Inject personalized interaction data
            if (interactionMap != null && interactionMap.TryGetValue(item.Id, out var interaction))
            {
                item.PersonalRating = interaction.Rating;
                item.IsFavorite = interaction.IsFavorite;
                item.Watched = interaction.IsWatched;
                item.PlaybackPosition = interaction.PlaybackPosition;
            }
        }
    }

    // items is always assigned by the cache/fallback paths above; the coalesce is
    // for the compiler's flow analysis (and safety if that invariant ever breaks).
    return items ?? Enumerable.Empty<MediaItemDto>();
}
}
