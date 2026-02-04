using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Orchestrates media scanning by routing requests to appropriate scanners.
/// </summary>
public interface IScannerOrchestrator
{
    /// <summary>
    /// Get the appropriate scanner for a library based on its type.
    /// </summary>
    /// <param name="library">The library to get a scanner for.</param>
    /// <returns>The scanner, or null if no scanner supports this library type.</returns>
    IMediaScanner? GetScannerForLibrary(Library library);

    /// <summary>
    /// Scan a library using the appropriate scanner.
    /// </summary>
    /// <param name="libraryId">ID of the library to scan.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ScanLibraryAsync(
        Guid libraryId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a library scan directly (Bypassing queue, internal use).
    /// </summary>
    Task ExecuteScanAsync(
        Guid libraryId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Process a single file addition or change.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="libraryId">ID of the library the file belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessSingleFileAsync(
        string filePath,
        Guid libraryId,
        CancellationToken cancellationToken = default);
}
