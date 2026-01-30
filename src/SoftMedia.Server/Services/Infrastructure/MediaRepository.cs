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
}
