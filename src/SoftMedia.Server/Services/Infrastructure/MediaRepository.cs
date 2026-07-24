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
        return await _context.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .ApplyLibraryAccessFilter(access)
            .CountAsync(e => e.SeriesId == seriesId && e.SeasonNumber == seasonNumber && e.Type == MediaType.Episode);
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
            .ThenBy(m => m.EpisodeNumber);

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

        return await query
            .Include(m => m.Series)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .OrderByDescending(m => m.DateAdded)
            .Take(limit * 25)
            .ToListAsync();
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
