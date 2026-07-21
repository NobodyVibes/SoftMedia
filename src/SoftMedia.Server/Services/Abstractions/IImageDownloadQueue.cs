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
    /// <param name="imageType">The type of image (Poster, Backdrop, etc.) for database updates.</param>
    /// <param name="personId">The provider's external person id for cast images.</param>
    /// <param name="libraryId">Owning library, when known. Counted in the per-library pending
    /// gauge that keeps a scan job's Metadata stage open until artwork finishes downloading.</param>
    Task EnqueueImageDownloadAsync(Guid mediaId, string remoteUrl, int? seasonNumber = null, int? episodeNumber = null, MediaType type = MediaType.Movie, ImageType imageType = ImageType.Poster, int? personId = null, Guid? libraryId = null);

    /// <summary>
    /// Number of downloads enqueued with a library id that have not finished yet.
    /// </summary>
    int GetPendingCountForLibrary(Guid libraryId);
}

public enum ImageType
{
    Poster,
    Backdrop,
    Still, // Episode still
    SeasonPoster,
    AlbumCover,
    ArtistImage,
    CastImage
}
