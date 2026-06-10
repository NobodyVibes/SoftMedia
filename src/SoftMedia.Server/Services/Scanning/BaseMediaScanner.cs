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

public record FileDiscoveryResult(string Path, long Size, DateTime LastWriteUtc);

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
        
        // Initialize striped locks (fixed memory usage)
        _stripedLocks = new SemaphoreSlim[LockStripeCount];
        for (int i = 0; i < LockStripeCount; i++)
        {
            _stripedLocks[i] = new SemaphoreSlim(1, 1);
        }
    }

    protected readonly IMetadataQueue _metadataQueue;

    /// <summary>
    /// Whether strict enrichment mode is enabled for this scan.
    /// Read once from MetadataEnrichmentMode setting at scan start.
    /// </summary>
    protected bool _strictEnrichment;

    /// <summary>
    /// Striped locks for parent entities to ensure thread safety without unbounded memory growth.
    /// </summary>
    private readonly SemaphoreSlim[] _stripedLocks;
    private const int LockStripeCount = 1024;
    
    protected static readonly SemaphoreSlim _dbWriteLock = new(1, 1);

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
            
        // Reset stats
        // _parentLocks no longer used (replaced by striped locks which persist)

        // Stats tracking (thread-safe)
        int processedCount = 0;
        int newCount = 0;
        int updatedCount = 0;
        int skippedCount = 0;
        int totalDirs = 0;

        try
        {
            // 0. Read enrichment mode setting once per scan (avoids per-file DB lookup)
            using (var settingsScope = _scopeFactory.CreateScope())
            {
                var settingsService = settingsScope.ServiceProvider.GetRequiredService<ISettingsService>();
                var enrichmentMode = await settingsService.GetSettingAsync("MetadataEnrichmentMode", "Relaxed");
                _strictEnrichment = enrichmentMode == "Strict";
            }

            // 1. Enumerate all directories first (Producer)
            var directories = EnumerateDirectories(library.Paths).ToList();
            totalDirs = directories.Count;
            _logger.LogInformation("[{Scanner}] Found {Count} directories to process", DisplayName, totalDirs);

            // 1.5. Build an O(1) bulk dictionary lookup to prevent N+1 queries during parallel scan
            _logger.LogDebug("[{Scanner}] Bulk-loading existing media items into memory for library '{LibraryName}'", DisplayName, library.Name);
            var knownFilesCache = new ConcurrentDictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
            using (var initScope = _scopeFactory.CreateScope())
            {
                var initContext = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var allItems = await initContext.MediaItems
                    .AsNoTracking()
                    .Where(m => m.LibraryId == library.Id && m.Path != null)
                    .ToListAsync(cancellationToken);
                    
                foreach(var item in allItems)
                {
                    knownFilesCache[item.Path!] = item;
                }
            }

            // 2. Record scan start time for Db-driven orphan detection
            var scanStartTime = DateTime.UtcNow;
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

                foreach (var fileResult in filesInDir)
                {
                    var filePath = fileResult.Path;
                    ct.ThrowIfCancellationRequested();
                    try 
                    {
                        // Look up existing item using O(1) in-memory cache
                        MediaItem? existing = null;
                        if (knownFilesCache.TryGetValue(filePath, out var cachedItem))
                        {
                            existing = cachedItem;
                            
                            // Safe attachment: Check if context is already tracking an item with this ID.
                            // This prevents conflicts if multiple threads/files share parent entities.
                            var tracked = context.ChangeTracker.Entries<MediaItem>()
                                .FirstOrDefault(e => e.Entity.Id == existing.Id);

                            if (tracked == null)
                            {
                                context.Attach(existing);
                            }
                            else
                            {
                                existing = tracked.Entity;
                            }
                            
                            existing.LastScannedUtc = DateTime.UtcNow;
                        }

                        var opResult = await ProcessFileAsync(context, fileResult, existing, library, ct);

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
                
                // Wrap Db SaveChanges with lock to fix SQLite concurrent writer panics
                await _dbWriteLock.WaitAsync(ct);
                try
                {
                    await context.SaveChangesAsync(ct);
                }
                finally
                {
                    _dbWriteLock.Release();
                }
                
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
                await CleanupOrphansAsync(context, library, scanStartTime, cancellationToken);
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

    protected virtual IEnumerable<FileDiscoveryResult> EnumerateFilesCurrentDir(string dirPath)
    {
        var dirInfo = new DirectoryInfo(dirPath);
        if (!dirInfo.Exists) yield break;
        
        IEnumerable<FileInfo> files = Enumerable.Empty<FileInfo>();
        try
        {
            files = dirInfo.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly);
        }
        catch { /* Permission ignored */ }

        foreach (var file in files)
        {
            if (CanHandleFile(file.FullName)) 
            {
                yield return new FileDiscoveryResult(file.FullName, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    protected async Task<IDisposable> LockParentAsync(string parentName, CancellationToken ct)
    {
        // Use striped locking to avoid dictionary overhead and unbounded growth
        var hash = (uint)parentName.GetHashCode(StringComparison.OrdinalIgnoreCase); 
        var index = hash % (uint)LockStripeCount;
        
        var sem = _stripedLocks[index];
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

        var fileInfo = new FileInfo(filePath);
        var fileResult = new FileDiscoveryResult(fileInfo.FullName, fileInfo.Exists ? fileInfo.Length : 0, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);

        var opResult = await ProcessFileAsync(context, fileResult, existing, library, cancellationToken);
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
        // Security (audit H2/M2): never admit a file whose path could inject ffmpeg/ffprobe
        // arguments downstream (a double-quote / control char in the name). Such names are
        // vanishingly rare for legitimate media, so skipping them is safe.
        if (Helpers.MediaPathSafety.HasArgumentInjectionRisk(filePath))
        {
            _logger.LogWarning("[{Scanner}] Skipping file with unsafe characters in path: {FilePath}", DisplayName, filePath);
            return false;
        }

        var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remove items from database that no longer exist on disk.
    /// Uses database-level ExecutionDeleteAsync for ultra-high performance.
    /// Containers (Series, Albums) are excluded since they don't map cleanly to leaf file paths.
    /// </summary>
    protected async Task CleanupOrphansAsync(
        AppDbContext context,
        Library library,
        DateTime scanStartTime,
        CancellationToken cancellationToken)
    {
        var containerTypes = new[]
        {
            MediaType.Series,
            MediaType.Season,
            MediaType.Artist,
            MediaType.Album
        };

        int deletedCount = 0;

        // ExecuteDeleteAsync is highly performant but unsupported by InMemory test provider
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var orphans = await context.MediaItems
                .Where(m => m.LibraryId == library.Id 
                         && !containerTypes.Contains(m.Type) 
                         && m.LastScannedUtc < scanStartTime)
                .ToListAsync(cancellationToken);
                
            if (orphans.Count > 0)
            {
                context.MediaItems.RemoveRange(orphans);
                deletedCount = orphans.Count;
            }
        }
        else
        {
            deletedCount = await context.MediaItems
                .Where(m => m.LibraryId == library.Id 
                         && !containerTypes.Contains(m.Type) 
                         && m.LastScannedUtc < scanStartTime)
                .ExecuteDeleteAsync(cancellationToken);
        }
            
        if (deletedCount > 0)
        {
            _logger.LogInformation("[{Scanner}] Bulk removed {Count} orphans", DisplayName, deletedCount);
        }
    }

    /// <summary>
    /// Process a single file. Implemented by concrete scanners.
    /// Returns ScanOperationResult indicating whether item was new, updated, or skipped, and if metadata refresh is needed.
    /// </summary>
    protected abstract Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
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
