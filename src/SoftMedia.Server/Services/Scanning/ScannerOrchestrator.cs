using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Routes scan requests to the appropriate scanner based on library type.
/// </summary>
public class ScannerOrchestrator : IScannerOrchestrator
{
    private readonly IEnumerable<IMediaScanner> _scanners;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScannerOrchestrator> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ScannerOrchestrator(
        IEnumerable<IMediaScanner> scanners,
        IServiceScopeFactory scopeFactory,
        ILogger<ScannerOrchestrator> logger,
        IServiceProvider serviceProvider)
    {
        _scanners = scanners;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public IMediaScanner? GetScannerForLibrary(Library library)
    {
        var scanner = _scanners.FirstOrDefault(s => s.SupportedType == library.Type);
        
        if (scanner == null)
        {
            _logger.LogWarning("No scanner found for library type: {LibraryType}", library.Type);
        }
        
        return scanner;
    }

    /// <inheritdoc/>
    public async Task ScanLibraryAsync(
        Guid libraryId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var library = await context.Libraries.FindAsync(new object[] { libraryId }, cancellationToken);
        
        if (library == null)
        {
            _logger.LogError("Library not found: {LibraryId}", libraryId);
            return;
        }

        // Lazy resolve to avoid circular dependency
        var queue = _serviceProvider.GetRequiredService<ILibraryScanQueueService>();
        queue.EnqueueScan(libraryId, library.Name);
        _logger.LogInformation("Enqueued scan for library '{LibraryName}'", library.Name);
    }

    /// <inheritdoc/>
    public async Task ExecuteScanAsync(
        Guid libraryId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var library = await context.Libraries.FindAsync(new object[] { libraryId }, cancellationToken);
        if (library == null)
        {
            _logger.LogError("Library not found: {LibraryId}", libraryId);
            throw new InvalidOperationException($"Library not found: {libraryId}");
        }

        var scanner = GetScannerForLibrary(library);
        if (scanner == null)
        {
            _logger.LogWarning("No scanner available for library '{LibraryName}' (type: {LibraryType})",
                library.Name, library.Type);
            return;
        }

        _logger.LogInformation("Executing scan for library '{LibraryName}' with {ScannerName}",
            library.Name, scanner.DisplayName);

        await scanner.ScanLibraryAsync(library, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ProcessSingleFileAsync(
        string filePath,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var library = await context.Libraries.FindAsync(new object[] { libraryId }, cancellationToken);
        if (library == null)
        {
            _logger.LogWarning("Library not found for single file processing: {LibraryId}", libraryId);
            return;
        }

        var scanner = GetScannerForLibrary(library);
        if (scanner == null)
        {
            _logger.LogDebug("No scanner for file: {FilePath}", filePath);
            return;
        }

        if (!scanner.CanHandleFile(filePath))
        {
            _logger.LogDebug("Scanner {ScannerName} cannot handle file: {FilePath}",
                scanner.DisplayName, filePath);
            return;
        }

        await scanner.ProcessSingleFileAsync(filePath, library, cancellationToken);

        try
        {
            var libraryService = scope.ServiceProvider.GetRequiredService<ILibraryService>();
            await libraryService.UpdateRecentlyAddedCacheAsync(libraryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update recently added cache for library {LibraryId}", libraryId);
        }
    }
}
