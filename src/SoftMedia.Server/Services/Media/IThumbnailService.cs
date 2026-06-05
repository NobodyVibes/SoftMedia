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
}
