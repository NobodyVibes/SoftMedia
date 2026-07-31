using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface IMediaRepository
{
    /// <summary>
    /// Retrieves a MediaItem by ID, including its associated Library.
    /// </summary>
    Task<MediaItem?> GetByIdWithLibraryAsync(Guid id);

    /// <summary>
    /// Retrieves a MediaItem by ID.
    /// </summary>
    Task<MediaItem?> GetByIdAsync(Guid id);

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
    /// DV-WI-015 — live (non-missing) member count per version group, one query for a
    /// whole page of collapsed rows.
    /// </summary>
    Task<Dictionary<Guid, int>> GetVersionCountsAsync(IEnumerable<Guid> versionGroupIds);

    /// <summary>
    /// Checks if a MediaItem exists.
    /// </summary>
    Task<bool> ExistsAsync(Guid id);

    Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetSeriesEpisodesWithInteractionsAsync(Guid seriesId, Guid userId);
    Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetComicIssuesWithInteractionsAsync(Guid seriesId, Guid userId);
    Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetArtistAlbumsWithInteractionsAsync(Guid artistId, Guid userId);
    Task<IEnumerable<(MediaItem Media, UserMediaInteraction? Interaction)>> GetAlbumTracksWithInteractionsAsync(Guid albumId, Guid userId);
    
    Task<IEnumerable<(Guid Id, MediaType Type)>> GetMediaIdsAndTypesByLibraryAsync(Guid libraryId);

    /// <summary>
    /// Retrieves recent media items, optionally filtered by library type.
    /// </summary>
    Task<IEnumerable<MediaItem>> GetRecentMediaAsync(int limit, LibraryType? type);

    /// <summary>
    /// Retrieves all episodes for a series.
    /// </summary>
    Task<IEnumerable<MediaItem>> GetEpisodesAsync(Guid seriesId);

    /// <summary>
    /// Retrieves multiple media items by ID.
    /// </summary>
    Task<IEnumerable<MediaItem>> GetByIdsAsync(IEnumerable<Guid> ids);
}
