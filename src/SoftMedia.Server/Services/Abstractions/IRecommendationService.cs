using SoftMedia.Server.Models;
using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Services.Abstractions;

public interface IRecommendationService
{
    Task<NextEpisodeResponse?> GetNextEpisodeAsync(Guid userId, Guid seriesId);
    Task<NextEpisodeResponse?> GetNextEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId);
    Task<NextEpisodeResponse?> GetPreviousEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId);

    /// <summary>
    /// Recommendations for the player's end-of-movie overlay: unfinished same-collection movies
    /// first, then genre-similar ones. Null when the movie doesn't exist or isn't visible to the
    /// caller (anti-probe: same response as a nonexistent id).
    /// </summary>
    Task<PostPlayResponse?> GetMoviePostPlayAsync(Guid userId, Guid movieId, int limit = 8);

    /// <summary>
    /// R-WI-020 — personalized home rows derived from the caller's play history
    /// (genre/collection affinity), ACL/rating-filtered at the query. Empty for a
    /// user with no history (the client self-suppresses).
    /// </summary>
    Task<IReadOnlyList<HomeRowDto>> GetHomeRowsAsync(Guid userId, int itemsPerRow = 15);

    // Hero Section
    Task UpdateHeroCacheAsync();
    Task<IEnumerable<MediaItemDto>> GetHeroItemsAsync();
}
