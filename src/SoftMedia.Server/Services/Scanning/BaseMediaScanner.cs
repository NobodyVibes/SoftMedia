using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Base class for media scanners providing shared functionality.
/// Uses the template method pattern for consistent scan behavior.
/// </summary>
public abstract class BaseMediaScanner : IMediaScanner
{
    protected readonly IServiceScopeFactory _scopeFactory;
    protected readonly ILogger _logger;
    protected readonly IMediaNotificationService _notificationService;

    public abstract LibraryType SupportedType { get; }
    public abstract string[] SupportedExtensions { get; }
    public abstract string DisplayName { get; }

    protected BaseMediaScanner(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        IMediaNotificationService notificationService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Template method for scanning a library.
    /// </summary>
    public virtual async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[{Scanner}] Starting scan of library '{LibraryName}' (ID: {LibraryId})",
            DisplayName, library.Name, library.Id);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // 1. Pre-load existing paths for O(1) lookup
            var existingPaths = await GetExistingPathsAsync(context, library.Id, cancellationToken);
            _logger.LogDebug("[{Scanner}] Found {Count} existing items in database", DisplayName, existingPaths.Count);

            // 2. Enumerate media files
            var files = EnumerateMediaFiles(library.Paths).ToList();
            _logger.LogInformation("[{Scanner}] Found {Count} media files to process", DisplayName, files.Count);

            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedCount = 0;
            var newCount = 0;
            var updatedCount = 0;
            var skippedCount = 0;

            // 3. Process each file
            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var existing = existingPaths.TryGetValue(filePath, out var id)
                        ? await context.MediaItems.FindAsync(new object[] { id }, cancellationToken)
                        : null;

                    var result = await ProcessFileAsync(context, filePath, existing, library, cancellationToken);
                    processedFiles.Add(filePath);
                    
                    // Track result counters
                    switch (result)
                    {
                        case ScanResult.New:
                            newCount++;
                            break;
                        case ScanResult.Updated:
                            updatedCount++;
                            break;
                        case ScanResult.Skipped:
                            skippedCount++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Scanner}] Error processing file: {FilePath}", DisplayName, filePath);
                }

                processedCount++;
                progress?.Report(new ScanProgress(
                    processedCount,
                    files.Count,
                    Path.GetFileName(filePath),
                    "Scanning files",
                    newCount,
                    updatedCount,
                    skippedCount));
            }

            // 4. Save changes from file processing
            await context.SaveChangesAsync(cancellationToken);

            // 5. Cleanup orphaned items (files that no longer exist)
            progress?.Report(new ScanProgress(files.Count, files.Count, null, "Cleaning up orphans", newCount, updatedCount, skippedCount));
            await CleanupOrphansAsync(context, library, existingPaths, processedFiles, cancellationToken);

            // 6. Cleanup empty containers (albums, series, etc.)
            progress?.Report(new ScanProgress(files.Count, files.Count, null, "Cleaning up empty containers", newCount, updatedCount, skippedCount));
            await CleanupEmptyContainersAsync(context, library, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("[{Scanner}] Completed scan of library '{LibraryName}'. Processed {Count} files. New: {New}, Updated: {Updated}, Skipped: {Skipped}",
                DisplayName, library.Name, processedCount, newCount, updatedCount, skippedCount);

            // 7. Notify UI of completed scan
            _notificationService.NotifyScanProgress(library.Id, processedCount, processedCount, "Scan complete");
            
            // Final progress report with all counts
            progress?.Report(new ScanProgress(processedCount, files.Count, null, "Complete", newCount, updatedCount, skippedCount));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Scanner}] Scan cancelled for library '{LibraryName}'", DisplayName, library.Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Scanner}] Error scanning library '{LibraryName}'", DisplayName, library.Name);
            throw;
        }
    }

    /// <summary>
    /// Process a single file (for file watcher events).
    /// </summary>
    public async Task ProcessSingleFileAsync(
        string filePath,
        Library library,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandleFile(filePath))
        {
            _logger.LogDebug("[{Scanner}] Ignoring file with unsupported extension: {FilePath}", DisplayName, filePath);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await context.MediaItems
            .FirstOrDefaultAsync(m => m.Path == filePath && m.LibraryId == library.Id, cancellationToken);

        await ProcessFileAsync(context, filePath, existing, library, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // Notify that an item was updated - use empty guid as placeholder
        // The actual item notification should happen in ProcessFileAsync
        _notificationService.NotifyScanProgress(library.Id, 1, 1, "File processed");
    }

    /// <summary>
    /// Check if this scanner can handle a file based on extension.
    /// </summary>
    public bool CanHandleFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get existing media item paths for the library.
    /// </summary>
    protected async Task<Dictionary<string, Guid>> GetExistingPathsAsync(
        AppDbContext context,
        Guid libraryId,
        CancellationToken cancellationToken)
    {
        return await context.MediaItems
            .Where(m => m.LibraryId == libraryId && m.Path != null)
            .Select(m => new { m.Path, m.Id })
            .ToDictionaryAsync(
                m => m.Path!,
                m => m.Id,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);
    }

    /// <summary>
    /// Enumerate media files in the library paths.
    /// </summary>
    protected IEnumerable<string> EnumerateMediaFiles(List<string> libraryPaths)
    {
        foreach (var path in libraryPaths)
        {
            if (!Directory.Exists(path))
            {
                _logger.LogWarning("[{Scanner}] Library path does not exist: {Path}", DisplayName, path);
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Scanner}] Error enumerating files in: {Path}", DisplayName, path);
                continue;
            }

            foreach (var file in files)
            {
                if (CanHandleFile(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Remove items from database that no longer exist on disk.
    /// </summary>
    protected async Task CleanupOrphansAsync(
        AppDbContext context,
        Library library,
        Dictionary<string, Guid> existingPaths,
        HashSet<string> processedFiles,
        CancellationToken cancellationToken)
    {
        var orphanPaths = existingPaths.Keys
            .Where(p => !processedFiles.Contains(p))
            .ToList();

        if (orphanPaths.Count == 0) return;

        _logger.LogInformation("[{Scanner}] Removing {Count} orphaned items", DisplayName, orphanPaths.Count);

        foreach (var path in orphanPaths)
        {
            var id = existingPaths[path];
            var item = await context.MediaItems.FindAsync(new object[] { id }, cancellationToken);
            if (item != null)
            {
                context.MediaItems.Remove(item);
                _logger.LogDebug("[{Scanner}] Removed orphan: {Path}", DisplayName, path);
            }
        }
    }

    /// <summary>
    /// Process a single file. Implemented by concrete scanners.
    /// Returns ScanResult indicating whether item was new, updated, or skipped.
    /// </summary>
    protected abstract Task<ScanResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cleanup empty container items (albums without tracks, series without episodes, etc.).
    /// Override in scanners that use hierarchical structures.
    /// </summary>
    protected virtual Task CleanupEmptyContainersAsync(
        AppDbContext context,
        Library library,
        CancellationToken cancellationToken)
    {
        // Default: no containers to clean up
        return Task.CompletedTask;
    }
}
