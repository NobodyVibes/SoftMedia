using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface IImageDownloadQueue
{
    /// <summary>
    /// Enqueues an image download request.
    /// </summary>
    /// <param name="mediaId">The ID of the media item.</param>
    /// <param name="remoteUrl">The remote URL of the image to download.</param>
    /// <param name="type">The type of media item (affects storage path).</param>
    /// <param name="imageType">The type of image (Poster, Backdrop, etc.) for MetadataJson updates.</param>
    Task EnqueueImageDownloadAsync(Guid mediaId, string remoteUrl, int? seasonNumber = null, int? episodeNumber = null, MediaType type = MediaType.Movie, ImageType imageType = ImageType.Poster);
}

public enum ImageType
{
    Poster,
    Backdrop,
    Still, // Episode still
    SeasonPoster,
    AlbumCover,
    ArtistImage
}
