using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Infrastructure;

public class MediaRepository : IMediaRepository
{
    private readonly AppDbContext _context;

    public MediaRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<MediaItem?> GetByIdWithLibraryAsync(Guid id)
    {
        return await _context.MediaItems
            .Include(m => m.Library)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<MediaItem?> GetByIdAsync(Guid id)
    {
        return await _context.MediaItems.FindAsync(id);
    }

    public async Task<IEnumerable<MediaItem>> GetSeriesSeasonsAsync(Guid seriesId)
    {
         return await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Season)
            .OrderBy(m => m.SeasonNumber)
            .ToListAsync();
    }

    public async Task<List<int>> GetDistinctSeasonNumbersAsync(Guid seriesId)
    {
        return await _context.MediaItems.AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .Select(m => m.SeasonNumber ?? 1)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();
    }
    
    public async Task<int> GetEpisodeCountAsync(Guid seriesId, int seasonNumber)
    {
        return await _context.MediaItems
            .CountAsync(e => e.SeriesId == seriesId && e.SeasonNumber == seasonNumber && e.Type == MediaType.Episode);
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        return await _context.MediaItems.AnyAsync(m => m.Id == id);
    }

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetSeriesEpisodesWithInteractionsAsync(Guid seriesId, Guid userId)
    {
        var query = _context.MediaItems.AsNoTracking()
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

    public async Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetArtistAlbumsWithInteractionsAsync(Guid artistId, Guid userId)
    {
        var query = _context.MediaItems.AsNoTracking()
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
        var query = _context.MediaItems.AsNoTracking()
            .Where(m => m.AlbumId == albumId && m.Type == MediaType.Audio)
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
        var items = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == libraryId)
            .Select(m => new { m.Id, m.Type })
            .ToListAsync();

        return items.Select(x => (x.Id, x.Type)).ToList();
    }

    public async Task<IEnumerable<MediaItem>> GetRecentMediaAsync(int limit, LibraryType? type)
    {
        IQueryable<MediaItem> query = _context.MediaItems.AsNoTracking();

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
            .OrderByDescending(m => m.DateAdded)
            .Take(limit * 25)
            .ToListAsync();
    }

    public async Task<IEnumerable<MediaItem>> GetEpisodesAsync(Guid seriesId)
    {
        return await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();
    }

    public async Task<IEnumerable<MediaItem>> GetByIdsAsync(IEnumerable<Guid> ids)
    {
        return await _context.MediaItems
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();
    }
}
