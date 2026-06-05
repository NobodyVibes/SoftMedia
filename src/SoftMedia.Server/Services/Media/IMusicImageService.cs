namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for resolving and serving music-related images (album covers, artist images).
/// </summary>
public interface IMusicImageService
{
    /// <summary>
    /// Get the cover art path for a track (resolves via album).
    /// </summary>
    Task<string?> GetTrackCoverPathAsync(Guid trackId);

    /// <summary>
    /// Get the cover art path for an album.
    /// </summary>
    Task<string?> GetAlbumCoverPathAsync(Guid albumId);

    /// <summary>
    /// Get the image path for an artist.
    /// </summary>
    Task<string?> GetArtistImagePathAsync(Guid artistId);

    /// <summary>
    /// Get the validated file path and MIME type for a media item's image.
    /// Handles path resolution, validation, and fallback logic.
    /// </summary>
    /// <param name="mediaItemId">The ID of the media item (track, album, or artist).</param>
    /// <returns>Tuple of (path, mimeType) or null if not found.</returns>
    Task<(string Path, string MimeType)?> GetImageInfoAsync(Guid mediaItemId);

    /// <summary>
    /// Get image bytes and MIME type for a media item.
    /// Handles path validation and fallback logic.
    /// </summary>
    /// <param name="mediaItemId">The ID of the media item (track, album, or artist).</param>
    /// <returns>Tuple of (bytes, mimeType) or null if not found.</returns>
    Task<(byte[] Data, string MimeType)?> GetImageBytesAsync(Guid mediaItemId);
}
