namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Produces cached, JPEG-encoded thumbnails of individual pages inside a
/// comic archive (CBZ or CBR). Separate from <c>IThumbnailService</c> because
/// the cache key is per-page rather than per-media-item — a 300-page comic
/// yields 300 distinct entries, and single-image-per-media assumptions don't
/// translate.
/// </summary>
public interface IComicPageThumbnailService
{
    /// <summary>
    /// Returns a JPEG-encoded thumbnail of the requested archive page at the
    /// target width. Returns null when the archive has no such page.
    /// </summary>
    /// <param name="archivePath">Absolute path to the CBZ/CBR file.</param>
    /// <param name="pageNumber">1-based page index within the archive.</param>
    /// <param name="width">Target width in pixels; height scales proportionally.</param>
    Task<byte[]?> GetAsync(
        string archivePath,
        int pageNumber,
        int width,
        CancellationToken cancellationToken = default);
}
