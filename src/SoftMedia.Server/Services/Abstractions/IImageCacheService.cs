using System.Threading.Tasks;

namespace SoftMedia.Server.Services.Abstractions;

public interface IImageCacheService
{
    Task<string> CacheSeriesPosterAsync(Guid seriesId, string remoteUrl);
    Task<string> CacheCastImageAsync(int personId, string remoteUrl);
    Task<string> CacheEpisodeStillAsync(Guid seriesId, int season, int episode, string remoteUrl);
    Task<string> CacheSeasonPosterAsync(Guid seriesId, int season, string remoteUrl);
    Task<string> CacheMoviePosterAsync(Guid movieId, string remoteUrl);
    Task<string> CacheAlbumCoverAsync(Guid albumId, string remoteUrl);
    Task<string> CacheGamePosterAsync(Guid gameId, string remoteUrl);
    void DeleteImageForMediaItem(Guid mediaItemId, Models.MediaType type);
    void DeleteImagesForLibrary(IEnumerable<(Guid Id, Models.MediaType Type)> mediaItems);
    void DeleteCastImagesForPersonIds(IEnumerable<int> personIds);
    int CleanupOrphanedImages(HashSet<Guid> validMediaIds);
}
