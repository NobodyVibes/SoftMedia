namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Extracts individual image pages from comic archive files (CBZ and CBR) for
/// on-demand serving. CBZ is read via <see cref="System.IO.Compression.ZipArchive"/>;
/// CBR is read via SharpCompress. Encrypted or malformed archives surface as
/// exceptions to the caller — controllers catch them and return a clean 500.
/// </summary>
public interface IComicArchiveService
{
    /// <summary>
    /// True if the given path uses a supported comic archive extension (.cbz or .cbr).
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
