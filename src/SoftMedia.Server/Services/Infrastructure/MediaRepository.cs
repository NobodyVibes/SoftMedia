using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Infrastructure;

public class MediaRepository : IMediaRepository
{
    private readonly AppDbContext _context;
    private readonly IUserContentRatingProvider _ratingProvider;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;

    public MediaRepository(
        AppDbContext context,
        IUserContentRatingProvider ratingProvider,
        IUserLibraryAccessProvider libraryAccessProvider)
    {
        _context = context;
        _ratingProvider = ratingProvider;
        _libraryAccessProvider = libraryAccessProvider;
    }

    public async Task<MediaItem?> GetByIdWithLibraryAsync(Guid id)
    {
        // Parental-control gate: filter applies to user-facing reads. Background
        // services (scanners) get UserRatingCeilings.Unrestricted, so they bypass
        // automatically. Direct stream-by-ID flows through MediaService.GetStreamInfoAsync
        // which calls this method — null return + 404 anti-probe handled upstream.
        // Wave C — also gate on per-library ACL.
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Include(m => m.Library)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<MediaItem?> GetByIdAsync(Guid id)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<IEnumerable<MediaItem>> GetSeriesSeasonsAsync(Guid seriesId)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems.AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Season)
            .OrderBy(m => m.SeasonNumber)
            .ToListAsync();
    }

    public async Task<List<int>> GetDistinctSeasonNumbersAsync(Guid seriesId)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems.AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .Select(m => m.SeasonNumber ?? 1)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();
    }

    public async Task<int> GetEpisodeCountAsync(Guid seriesId, int seasonNumber)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        // DV-WI-003: count EPISODES, not files — duplicate copies of one episode share an
        // EpisodeNumber and must not inflate the season count (10 episodes + 1 dup = 10).
        // Only REAL numbers dedupe: null/0 means "couldn't parse an episode number", where
        // several distinct files legitimately share the bucket and must each count.
        var numbers = await _context.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Where(e => e.SeriesId == seriesId && e.SeasonNumber == seasonNumber && e.Type == MediaType.Episode)
            .Select(e => e.EpisodeNumber)
            .ToListAsync();
        return numbers.Where(n => n is > 0).Distinct().Count()
             + numbers.Count(n => n is null or <= 0);
    }

    public async Task<Dictionary<Guid, int>> GetVersionCountsAsync(IEnumerable<Guid> versionGroupIds)
    {
        var ids = versionGroupIds.Distinct().Cast<Guid?>().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();

        var rows = await _context.MediaItems.AsNoTracking()
            .Where(m => m.VersionGroupId != null && ids.Contains(m.VersionGroupId) && !m.IsMissing)
            .GroupBy(m => m.VersionGroupId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        // ACL applies — existence checks must respect the same rules as reads
        // so callers can't probe whether a media id exists in a blocked library.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems
            .ApplyLibraryAccessFilter(access)
            .AnyAsync(m => m.Id == id);
    }

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetSeriesEpisodesWithInteractionsAsync(Guid seriesId, Guid userId)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var query = _context.MediaItems.AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ThenBy(m => m.Id); // DV-WI-001: stable order for duplicate copies of one episode

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userId } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();
        return items.Select(x => (x.Media, (UserMediaInteraction?)x.Interaction)).ToList();
    }

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetComicIssuesWithInteractionsAsync(Guid seriesId, Guid userId)
    {
        // Issue number lives in the EpisodeNumber column. Null-number one-shots sort
        // last by date; numbered issues sort ascending.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var query = _context.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.ComicIssue)
            .OrderBy(m => m.EpisodeNumber == null)
            .ThenBy(m => m.EpisodeNumber)
            .ThenBy(m => m.DateAdded);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userId } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();
        return items.Select(x => (x.Media, (UserMediaInteraction?)x.Interaction)).ToList();
    }

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetArtistAlbumsWithInteractionsAsync(Guid artistId, Guid userId)
    {
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var query = _context.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.ArtistId == artistId && m.Type == MediaType.Album)
            .OrderByDescending(m => m.Year)
            .ThenBy(m => m.Title);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userId } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();
        return items.Select(x => (x.Media, (UserMediaInteraction?)x.Interaction)).ToList();
    }

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetAlbumTracksWithInteractionsAsync(Guid albumId, Guid userId)
    {
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var query = _context.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .Where(m => m.AlbumId == albumId && m.Type == MediaType.Audio)
            // B-03: the audio player bar reads metadata.artist/album (BuildNameContext),
            // which is only populated when these navigations are loaded — without them
            // album-page playback shows "Unknown Artist".
            .Include(m => m.Artist)
            .Include(m => m.Album)
            .OrderBy(m => m.DiscNumber)
            .ThenBy(m => m.TrackNumber);

        var joinedQuery = from m in query
                          join umi in _context.UserMediaInteractions.AsNoTracking()
                            on new { MediaItemId = m.Id, UserId = userId } equals new { umi.MediaItemId, umi.UserId } into umis
                          from umi in umis.DefaultIfEmpty()
                          select new { Media = m, Interaction = umi };

        var items = await joinedQuery.ToListAsync();
        return items.Select(x => (x.Media, (UserMediaInteraction?)x.Interaction)).ToList();
    }

    public async Task<IEnumerable<(Guid Id, MediaType Type)>> GetMediaIdsAndTypesByLibraryAsync(Guid libraryId)
    {
        // No ACL filter here — this method is used internally by
        // LibraryService.DeleteLibraryAsync (admin-only) for image-cache
        // cleanup. Filtering would risk leaving orphan files behind.
        var items = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == libraryId)
            .Select(m => new { m.Id, m.Type })
            .ToListAsync();

        return items.Select(x => (x.Id, x.Type)).ToList();
    }

    public async Task<IEnumerable<MediaItem>> GetRecentMediaAsync(int limit, LibraryType? type)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        IQueryable<MediaItem> query = _context.MediaItems.AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .ExcludeMissing();

        if (type.HasValue)
        {
            var libraryIds = await _context.Libraries
                .Where(l => l.Type == type.Value)
                .Select(l => l.Id)
                .ToListAsync();

            query = query.Where(m => libraryIds.Contains(m.LibraryId));
        }

        // SR-WI-065 (API-M7): roll episodes/seasons up to their series and audio
        // tracks up to their album IN the query. The old shape fetched limit*25
        // fully-hydrated rows (three Includes) so MediaRetrievalService could dedup
        // in memory — a burst of episodes both hydrated thousands of entities and
        // could starve other content out of the scan window. Semantics preserved:
        //  - Episode/Season → representative is the Series row; Audio → the Album row;
        //    everything else represents itself (incl. Series/Album rows directly).
        //  - Episodes without a series / tracks without an album are dropped, exactly
        //    as the old in-memory loop let them fall through every branch.
        //  - The representative's DateAdded is promoted to the newest underlying
        //    activity so the client 'NEW' badge fires for new episodes on old series.
        //  - Visibility (rating/ACL/missing) is decided on the UNDERLYING items;
        //    rollup parents are then hydrated unfiltered, matching the old code's
        //    unfiltered `Include(m => m.Series/Album)` navigation loads.
        // Grouping/CASE both translate on SQLite and evaluate on the InMemory provider.
        var representatives = await query
            .Where(m => ((m.Type != MediaType.Episode && m.Type != MediaType.Season) || m.SeriesId != null)
                     && (m.Type != MediaType.Audio || m.AlbumId != null))
            .Select(m => new
            {
                RepresentativeId =
                    (m.Type == MediaType.Episode || m.Type == MediaType.Season) ? m.SeriesId!.Value
                    : m.Type == MediaType.Audio ? m.AlbumId!.Value
                    : m.Id,
                m.DateAdded
            })
            .GroupBy(x => x.RepresentativeId)
            .Select(g => new { Id = g.Key, DateAdded = g.Max(x => x.DateAdded) })
            .OrderByDescending(x => x.DateAdded)
            .Take(limit)
            .ToListAsync();

        var ids = representatives.Select(r => r.Id).ToList();
        var hydrated = await _context.MediaItems.AsNoTracking()
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id);

        var result = new List<MediaItem>(representatives.Count);
        foreach (var rep in representatives)
        {
            // A dangling parent id (episode kept after its series row vanished)
            // simply drops out, like the old `item.Series != null` guard.
            if (!hydrated.TryGetValue(rep.Id, out var item)) continue;
            item.DateAdded = rep.DateAdded;
            result.Add(item);
        }
        return result;
    }

    public async Task<IEnumerable<MediaItem>> GetEpisodesAsync(Guid seriesId)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        // SR-WI-011 — missing episodes are skipped as next-up. All callers are the
        // next/previous-episode resolvers in RecommendationService, so filtering
        // here is safe (no detail-page surface reads this method).
        return await _context.MediaItems
            .AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .ExcludeMissing()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            // DV-WI-001: duplicate files of one episode share (Season, Episode); without a
            // total order their relative position is DB-arbitrary and next/previous-episode
            // navigation flips between copies across calls.
            .ThenBy(m => m.Id)
            .ToListAsync();
    }

    public async Task<IEnumerable<MediaItem>> GetByIdsAsync(IEnumerable<Guid> ids)
    {
        var ceilings = await _ratingProvider.GetCurrentAsync();
        var access = await _libraryAccessProvider.GetCurrentAsync();
        return await _context.MediaItems
            .AsNoTracking()
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();
    }
}
