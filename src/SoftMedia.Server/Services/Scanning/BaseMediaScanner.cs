using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

public record ScanOperationResult(ScanResult Result, Guid ItemId = default, bool EnqueueMetadata = false);

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
        IMediaNotificationService notificationService,
        IMetadataQueue metadataQueue)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notificationService = notificationService;
        _metadataQueue = metadataQueue;
    }

    protected readonly IMetadataQueue _metadataQueue;

    /// <summary>
    /// Template method for scanning a library.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _parentLocks = new(StringComparer.OrdinalIgnoreCase);

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
            
        // Reset locks for this scan
        _parentLocks.Clear();

        // Stats tracking (thread-safe)
        int processedCount = 0;
        int newCount = 0;
        int updatedCount = 0;
        int skippedCount = 0;
        int totalDirs = 0;

        try
        {
            // 1. Enumerate all directories first (Producer)
            var directories = EnumerateDirectories(library.Paths).ToList();
            totalDirs = directories.Count;
            _logger.LogInformation("[{Scanner}] Found {Count} directories to process", DisplayName, totalDirs);

            // 2. Pre-load existing paths strategy might need adjustment for massive libraries.
            // For now, we load ALL paths. Ideally, we'd load per directory, but that requires exact path matching.
            // Let's stick to loading all but using a concurrent dictionary?
            // Actually, we can fetch existing paths PER DIRECTORY inside the loop to enable massive libraries.
            // BUT, verifying duplicates/moves across directories is harder.
            // Let's keep the global "GetExistingPaths" call for now (assumed < 100k items).
            // We need a thread-safe way to read/remove from it.
            // We can make a ConcurrentDictionary or just lock it if we only read.
            // CleanupOrphans needs the remaining list.
            
            // NOTE: DbContext is NOT thread safe. We need a transient one for GetExistingPaths or use one scope.
            Dictionary<string, Guid> existingPaths;
            using (var scope = _scopeFactory.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                existingPaths = await GetExistingPathsAsync(ctx, library.Id, cancellationToken);
            }
            
            // Wrap existingPaths in a ConcurrentDictionary for partial updates/removals if needed?
            // Actually, we track "processedFiles" separately to indentify orphans.
            var processedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            // 3. Process Directories in Parallel (Consumer)
            var parallelOptions = new ParallelOptions 
            { 
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount 
            };

            await Parallel.ForEachAsync(directories, parallelOptions, async (dirPath, ct) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                // Get files in THIS directory only
                var filesInDir = EnumerateFilesCurrentDir(dirPath).ToList();
                if (filesInDir.Count == 0) return;

                int localNew = 0;
                int localUpdated = 0;
                int localSkipped = 0;

                // List for deferred metadata enqueueing
                var deferredQueue = new List<(Guid Id, LibraryType Type)>();

                foreach (var filePath in filesInDir)
                {
                    ct.ThrowIfCancellationRequested();
                    try 
                    {
                        var opResult = await ProcessFileAsync(context, filePath, null /* optimize lookups? */, library, ct);
                        
                        // Check if we need to look up existing item ID (if we passed null)
                        // Actually, ProcessFileAsync *usually* handles lookup if passed null? 
                        // Or we look it up here using our global map.
                        if (existingPaths.TryGetValue(filePath, out var id))
                        {
                            // We might need to attach it to context if updates are needed.
                            // ProcessFileAsync overrides usually do a DB lookup if 'existing' is null.
                        }

                        processedFiles.TryAdd(filePath, 0);

                        switch (opResult.Result)
                        {
                            case ScanResult.New: localNew++; break;
                            case ScanResult.Updated: localUpdated++; break;
                            case ScanResult.Skipped: localSkipped++; break;
                        }

                        if (opResult.EnqueueMetadata && opResult.ItemId != Guid.Empty)
                        {
                            deferredQueue.Add((opResult.ItemId, library.Type));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{Scanner}] Error processing file: {FilePath}", DisplayName, filePath);
                    }
                    
                    Interlocked.Increment(ref processedCount);
                    // Progress reporting needs to be throttled or it will flood SignalR
                    if (processedCount % 10 == 0)
                    {
                        progress?.Report(new ScanProgress(processedCount, -1, Path.GetFileName(dirPath), "Scanning..."));
                    }
                }
                
                await context.SaveChangesAsync(ct);
                
                // Process deferred queue AFTER save
                foreach (var item in deferredQueue)
                {
                    await _metadataQueue.EnqueueMetadataRefreshAsync(item.Id, item.Type);
                }
                
                Interlocked.Add(ref newCount, localNew);
                Interlocked.Add(ref updatedCount, localUpdated);
                Interlocked.Add(ref skippedCount, localSkipped);
            });

            // 5. Cleanup Orphans (Global Scope)
            using (var cleanupScope = _scopeFactory.CreateScope())
            {
                var context = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await CleanupOrphansAsync(context, library, existingPaths, new HashSet<string>(processedFiles.Keys), cancellationToken);
                await CleanupEmptyContainersAsync(context, library, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("[{Scanner}] Completed scan. Processed {Count}. New: {New}, Upd: {Upd}", DisplayName, processedCount, newCount, updatedCount);
            
            _notificationService.NotifyScanProgress(library.Id, processedCount, processedCount, "Scan complete");
            progress?.Report(new ScanProgress(processedCount, processedCount, null, "Complete", newCount, updatedCount, skippedCount));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Scanner}] Scan cancelled", DisplayName);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Scanner}] Error scanning library", DisplayName);
            throw;
        }
    }

    protected virtual IEnumerable<string> EnumerateDirectories(List<string> libraryPaths)
    {
         foreach (var path in libraryPaths)
         {
             if (Directory.Exists(path))
             {
                 yield return path; // The root itself
                 foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                 {
                     yield return dir;
                 }
             }
         }
    }

    protected virtual IEnumerable<string> EnumerateFilesCurrentDir(string dirPath)
    {
        if (!Directory.Exists(dirPath)) yield break;
        
        IEnumerable<string> files = Enumerable.Empty<string>();
        try
        {
            files = Directory.EnumerateFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly);
        }
        catch { /* Permission ignored */ }

        foreach (var file in files)
        {
            if (CanHandleFile(file)) yield return file;
        }
    }

    protected async Task<IDisposable> LockParentAsync(string parentName, CancellationToken ct)
    {
        var sem = _parentLocks.GetOrAdd(parentName, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new SemaphoreReleaser(sem);
    }
    
    private class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public SemaphoreReleaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
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

        var opResult = await ProcessFileAsync(context, filePath, existing, library, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        if (opResult.EnqueueMetadata && opResult.ItemId != Guid.Empty)
        {
             await _metadataQueue.EnqueueMetadataRefreshAsync(opResult.ItemId, library.Type);
        }

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
    protected virtual IEnumerable<string> EnumerateMediaFiles(List<string> libraryPaths)
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
    /// Returns ScanOperationResult indicating whether item was new, updated, or skipped, and if metadata refresh is needed.
    /// </summary>
    protected abstract Task<ScanOperationResult> ProcessFileAsync(
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
