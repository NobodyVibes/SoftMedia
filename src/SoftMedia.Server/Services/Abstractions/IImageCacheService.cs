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
    Task<string> CacheBookPosterAsync(Guid bookId, string remoteUrl);

    /// <summary>
    /// R-WI-014 — copy a LOCAL sidecar image (poster.jpg beside the media, an NFO-referenced
    /// file) into the image cache under the given cache key. Local keys are DISTINCT from
    /// provider keys ("movies/{id}_poster_local", never "movies/{id}_poster") so provider
    /// downloads and local copies can never shadow each other. The source must live under
    /// <paramref name="jailRoot"/> after full symlink resolution (a symlinked poster.jpg must
    /// not exfiltrate arbitrary readable files into the publicly served cache). Freshness is
    /// exact: the copy carries the source's mtime, and a matching (mtime, size) cached copy is
    /// reused. Returns the "/cache/images/..." web path, or null when the source is unusable.
    /// </summary>
    Task<string?> CacheLocalImageAsync(string sourceFilePath, string cacheKey, string jailRoot);

    /// <summary>R-WI-014 — delete every cached variant of a local-art key (sidecar removed).</summary>
    void DeleteCachedLocalImage(string cacheKey);

    void DeleteImageForMediaItem(Guid mediaItemId, Models.MediaType type);
    void DeleteImagesForLibrary(IEnumerable<(Guid Id, Models.MediaType Type)> mediaItems);
    void DeleteCastImagesForPersonIds(IEnumerable<int> personIds);
    int CleanupOrphanedImages(HashSet<Guid> validMediaIds);
}
