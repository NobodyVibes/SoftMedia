namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for generating and caching image thumbnails.
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Get or create a thumbnail for the given source image.
    /// Returns the path to the cached WebP thumbnail, or null if generation fails.
    /// </summary>
    /// <param name="sourcePath">Full path to the source image file.</param>
    /// <param name="mediaItemId">Media item ID used for cache key.</param>
    /// <param name="width">Target width in pixels. Height is calculated to maintain aspect ratio.</param>
    Task<string?> GetOrCreateThumbnailAsync(string sourcePath, Guid mediaItemId, int width);

    /// <summary>
    /// Delete every cached thumbnail width for a key ("{key}_*.webp"). Keys are either a
    /// media-item id or a proxy-derived guid (IProxyImageStore.GetThumbnailKey).
    /// Returns the number of files deleted.
    /// </summary>
    int DeleteThumbnails(Guid key);

    /// <summary>
    /// Delete thumbnails whose key matches no entry in <paramref name="validKeys"/> AND whose
    /// file is older than <paramref name="minAge"/>. The age guard exists because the
    /// thumbnails directory mixes two key spaces — media-item ids and proxy-derived guids —
    /// and proxy-derived keys can never appear in a valid-id set built from the database;
    /// without the guard every actively-used proxy thumbnail would be deleted and regenerated
    /// on each sweep. <paramref name="validKeys"/> must follow the row-existence contract
    /// (include IsMissing rows). Returns the number of files deleted.
    /// </summary>
    int CleanupOrphans(HashSet<Guid> validKeys, TimeSpan minAge);
}
