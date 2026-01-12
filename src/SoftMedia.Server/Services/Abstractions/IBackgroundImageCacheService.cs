namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Service for queueing media items for background image caching.
/// Images are downloaded and cached asynchronously without blocking library scans.
/// </summary>
public interface IBackgroundImageCacheService
{
    /// <summary>
    /// Queue a media item for background image caching.
    /// Duplicate IDs are automatically deduplicated.
    /// </summary>
    /// <param name="mediaItemId">The ID of the media item to cache images for.</param>
    void QueueImageCaching(Guid mediaItemId);
    
    /// <summary>
    /// Get the current queue depth for monitoring.
    /// </summary>
    int GetQueueDepth();
}
