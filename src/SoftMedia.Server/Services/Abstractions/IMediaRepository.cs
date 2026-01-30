using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface IMediaRepository
{
    /// <summary>
    /// Retrieves a MediaItem by ID, including its associated Library.
    /// </summary>
    Task<MediaItem?> GetByIdWithLibraryAsync(Guid id);

    /// <summary>
    /// Retrieves all Season items for a specific Series, ordered by season number.
    /// </summary>
    Task<IEnumerable<MediaItem>> GetSeriesSeasonsAsync(Guid seriesId);

    /// <summary>
    /// Retrieves distinct season numbers from episodes for a series (fallback mechanism).
    /// </summary>
    Task<List<int>> GetDistinctSeasonNumbersAsync(Guid seriesId);

    /// <summary>
    /// Gets the count of episodes for a specific season in a series.
    /// </summary>
    Task<int> GetEpisodeCountAsync(Guid seriesId, int seasonNumber);

    /// <summary>
    /// Checks if a MediaItem exists.
    /// </summary>
    Task<bool> ExistsAsync(Guid id);
}
