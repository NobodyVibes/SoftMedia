namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Extracts individual image pages from comic archive files (CBZ) for on-demand serving.
/// CBR (RAR) support requires a third-party library and is not yet implemented.
/// </summary>
public interface IComicArchiveService
{
    /// <summary>
    /// True if the given extension is a supported comic archive (currently cbz only).
    /// </summary>
    bool IsSupportedArchive(string filePath);

    /// <summary>
    /// Returns the total number of image pages in the archive.
    /// </summary>
    Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a single page (1-based) as a byte array plus its content type.
    /// Returns null if the page number is out of range.
    /// </summary>
    Task<ComicPage?> GetPageAsync(string filePath, int pageNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the embedded <c>ComicInfo.xml</c> from the archive root (if present) and
    /// returns a typed <see cref="Media.ComicInfoXml"/>. Matches filename case-insensitively
    /// per real-world ripper behaviour. Returns null when the file is absent or malformed.
    /// </summary>
    Task<Media.ComicInfoXml?> ExtractComicInfoAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed record ComicPage(byte[] Data, string ContentType);
