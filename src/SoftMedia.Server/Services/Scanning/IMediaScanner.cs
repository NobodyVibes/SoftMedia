using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Interface for media-type-specific library scanners.
/// Each scanner handles one type of media (music, TV, movies, etc.).
/// </summary>
public interface IMediaScanner
{
    /// <summary>
    /// The library type this scanner handles.
    /// </summary>
    LibraryType SupportedType { get; }

    /// <summary>
    /// File extensions this scanner can process (without leading dot).
    /// </summary>
    string[] SupportedExtensions { get; }

    /// <summary>
    /// Display name for logging and UI purposes.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Scan an entire library, processing all media files.
    /// </summary>
    /// <param name="library">The library to scan.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Process a single file (used for file watcher updates).
    /// </summary>
    /// <param name="filePath">Path to the file to process.</param>
    /// <param name="library">The library the file belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessSingleFileAsync(
        string filePath,
        Library library,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if this scanner can handle a specific file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>True if this scanner can handle the file.</returns>
    bool CanHandleFile(string filePath);
}
