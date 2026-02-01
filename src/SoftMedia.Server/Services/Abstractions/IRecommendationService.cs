using SoftMedia.Server.Models;
using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Services.Abstractions;

public interface IRecommendationService
{
    Task<NextEpisodeResponse?> GetNextEpisodeAsync(Guid userId, Guid seriesId);
    Task<NextEpisodeResponse?> GetNextEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId);
    Task<NextEpisodeResponse?> GetPreviousEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId);

    // Hero Section
    Task UpdateHeroCacheAsync();
    Task<IEnumerable<MediaItemDto>> GetHeroItemsAsync();
}
