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

    /// <summary>
    /// Delete cached cast headshots by the provider's EXTERNAL person ids (the on-disk key,
    /// "tv/cast/{Person.ExternalId}.{ext}") — never Person primary keys.
    /// </summary>
    void DeleteCastImagesForExternalIds(IEnumerable<int> externalPersonIds);

    /// <summary>
    /// Delete cast headshots whose external id is not in <paramref name="validExternalIds"/>
    /// (ids of persons still referenced by any MediaItemCast row, row-existence contract).
    /// Returns files deleted.
    /// </summary>
    int CleanupOrphanedCastImages(HashSet<int> validExternalIds);

    /// <summary>
    /// SR-WI-037 — delete orphaned cached artwork. The criterion for
    /// <paramref name="validMediaIds"/> is ROW-EXISTENCE, not visibility: callers MUST pass
    /// the ids of ALL MediaItems rows INCLUDING soft-deleted (<c>IsMissing</c>) ones — a
    /// missing item's artwork is retained so it heals when the drive returns. Only ids with
    /// no DB row at all are orphans. Returns the number of files deleted.
    /// </summary>
    int CleanupOrphanedImages(HashSet<Guid> validMediaIds);

    /// <summary>
    /// Every provider poster currently on disk, as media-item id → "/cache/images/…" web path.
    /// Only exact "{id}_poster.{ext}" keys are reported: season posters ("{id}_season01_poster"),
    /// album covers ("{id}_cover") and local-sidecar copies ("{id}_poster_local") are owned by
    /// other columns/flags and are deliberately excluded. Used to heal rows whose PosterUrl
    /// still points at the provider even though the art was already cached.
    /// </summary>
    IReadOnlyDictionary<Guid, string> GetCachedPosterPaths();

    /// <summary>
    /// SR-WI-037 — invalidate an item's cached PROVIDER artwork so the next Cache*Async call
    /// re-downloads it (otherwise a changed provider poster is served from cache forever).
    /// Deletes every cached file named "{mediaItemId}_*" across all cache subdirectories
    /// (tv, movies, music, games, books) EXCEPT local-sidecar copies ("*_local" keys,
    /// R-WI-014) — those are not provider-refreshable and would dangle until the next scan
    /// re-ingests them. For episode stills / season posters pass the SERIES id (their cache
    /// keys are keyed by series id). Intended caller: the per-item metadata-refresh admin
    /// endpoint (SR-WI-036). Returns the number of files deleted.
    /// </summary>
    Task<int> InvalidateCachedImagesAsync(Guid mediaItemId);
}
