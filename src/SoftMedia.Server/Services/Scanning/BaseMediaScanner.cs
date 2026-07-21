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
    /// Directories whose listing failed during this scan's discovery (permissions,
    /// transient I/O, depth cap). Their subtrees are shielded from orphan cleanup —
    /// not being able to list a directory says nothing about whether its files exist.
    /// Cleared at scan start; discovery is single-threaded so no locking is needed.
    /// </summary>
    protected readonly HashSet<string> _unreadableDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Striped locks for parent entities to ensure thread safety without unbounded memory growth.
    /// </summary>
    private readonly SemaphoreSlim[] _stripedLocks;
    private const int LockStripeCount = 1024;
    
    protected static readonly SemaphoreSlim _dbWriteLock = new(1, 1);

    /// <summary>
    /// Minimum interval between progress reports. The SignalR batcher dedups to
    /// latest-per-library at 500ms, so this only bounds adapter callback overhead.
    /// </summary>
    private const int ProgressReportIntervalMs = 250;

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

        // Stats tracking (thread-safe)
        int processedCount = 0;
        int newCount = 0;
        int updatedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        try
        {
            // 0. Read enrichment mode setting once per scan (avoids per-file DB lookup)
            using (var settingsScope = _scopeFactory.CreateScope())
            {
                var settingsService = settingsScope.ServiceProvider.GetRequiredService<ISettingsService>();
                var enrichmentMode = await settingsService.GetSettingAsync("MetadataEnrichmentMode", "Relaxed");
                _strictEnrichment = enrichmentMode == "Strict";
            }

            _unreadableDirs.Clear();

            // 0.5. Root availability guard. An unmounted drive or dropped network share must
            //      never masquerade as an empty library: with every root gone the scan would
            //      "complete" having seen zero files and orphan-purge the entire catalog.
            var missingRoots = library.Paths.Where(p => !RootExists(p)).ToList();
            if (library.Paths.Count > 0 && missingRoots.Count == library.Paths.Count)
            {
                throw new InvalidOperationException(
                    $"All library paths are unreachable ({string.Join(", ", missingRoots)}). " +
                    "Scan aborted so the library is not purged; check that the drive/share is mounted.");
            }
            if (missingRoots.Count > 0)
            {
                _logger.LogWarning(
                    "[{Scanner}] {Count} of {Total} library paths are unreachable ({Missing}); items under them will be preserved, not purged",
                    DisplayName, missingRoots.Count, library.Paths.Count, string.Join(", ", missingRoots));
            }

            // 1. Discovery: walk the tree once, capturing each directory's eligible files.
            //    Gives the scan an exact total up front (real percentages for the UI) and
            //    means the processing walk below never touches the filesystem for listings.
            progress?.Report(new ScanProgress(0, 0, null, "Discovering files...", Stage: LibraryScanStage.Discovery));
            var discovered = new List<(string Dir, List<FileDiscoveryResult> Files)>();
            int totalFiles = 0;
            long lastDiscoveryReport = Environment.TickCount64;
            foreach (var dirPath in EnumerateDirectories(library.Paths))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = EnumerateFilesCurrentDir(dirPath).ToList();
                if (files.Count == 0) continue;
                discovered.Add((dirPath, files));
                totalFiles += files.Count;

                var now = Environment.TickCount64;
                if (now - lastDiscoveryReport >= ProgressReportIntervalMs)
                {
                    lastDiscoveryReport = now;
                    progress?.Report(new ScanProgress(0, totalFiles, null,
                        $"Discovering files... ({totalFiles} found)", Stage: LibraryScanStage.Discovery));
                }
            }
            _logger.LogInformation("[{Scanner}] Discovered {Files} files in {Dirs} directories",
                DisplayName, totalFiles, discovered.Count);

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

            // 2. Orphan detection input: every file path discovered this scan. Items whose
            //    path is absent from this set no longer exist on disk. (Set-difference
            //    replaces the old LastScannedUtc timestamping, which forced an UPDATE of
            //    every row in the library on every scan just to mark items as "seen".)
            var seenPaths = new HashSet<string>(
                discovered.SelectMany(d => d.Files).Select(f => f.Path),
                StringComparer.OrdinalIgnoreCase);

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            // Time-based report throttle shared across the parallel walk. The old
            // "processedCount % 10" gate raced under parallelism (ticks were skipped)
            // and libraries under 10 files never reported at all.
            long lastProcessingReport = 0;
            void ReportProcessing(string? currentFileName)
            {
                if (progress == null) return;
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastProcessingReport);
                if (now - last < ProgressReportIntervalMs) return;
                if (Interlocked.CompareExchange(ref lastProcessingReport, now, last) != last) return;
                progress.Report(new ScanProgress(
                    Volatile.Read(ref processedCount), totalFiles, currentFileName, "Scanning files...",
                    Volatile.Read(ref newCount), Volatile.Read(ref updatedCount),
                    Volatile.Read(ref skippedCount), Volatile.Read(ref errorCount)));
            }

            await Parallel.ForEachAsync(discovered, parallelOptions, async (dir, ct) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // List for deferred metadata enqueueing
                var deferredQueue = new List<(Guid Id, LibraryType Type)>();

                foreach (var fileResult in dir.Files)
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

                            // Safe attachment: Check if context is already tracking an item with
                            // this ID (shared parents). LocalView.FindEntry is an O(1) lookup —
                            // enumerating ChangeTracker.Entries here was O(tracked²) per directory.
                            var tracked = context.MediaItems.Local.FindEntry(existing.Id);

                            if (tracked == null)
                            {
                                context.Attach(existing);
                            }
                            else
                            {
                                existing = tracked.Entity;
                            }
                        }

                        var opResult = await ProcessFileAsync(context, fileResult, existing, library, ct);

                        switch (opResult.Result)
                        {
                            case ScanResult.New: Interlocked.Increment(ref newCount); break;
                            case ScanResult.Updated: Interlocked.Increment(ref updatedCount); break;
                            case ScanResult.Skipped: Interlocked.Increment(ref skippedCount); break;
                        }

                        if (opResult.EnqueueMetadata && opResult.ItemId != Guid.Empty)
                        {
                            deferredQueue.Add((opResult.ItemId, library.Type));
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        _logger.LogWarning(ex, "[{Scanner}] Error processing file: {FilePath}", DisplayName, filePath);
                    }

                    Interlocked.Increment(ref processedCount);
                    ReportProcessing(Path.GetFileName(filePath));
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
                    await _metadataQueue.EnqueueMetadataRefreshAsync(item.Id, item.Type, libraryId: library.Id);
                }
            });

            // 5. Cleanup Orphans (Global Scope)
            progress?.Report(new ScanProgress(processedCount, totalFiles, null, "Cleaning up...",
                newCount, updatedCount, skippedCount, errorCount, LibraryScanStage.Finishing));
            using (var cleanupScope = _scopeFactory.CreateScope())
            {
                var context = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (_unreadableDirs.Count > 0)
                {
                    _logger.LogWarning(
                        "[{Scanner}] {Count} directories could not be listed this scan; their subtrees are preserved, not purged: {Dirs}",
                        DisplayName, _unreadableDirs.Count, string.Join(", ", _unreadableDirs.Take(10)));
                }
                var shieldedPaths = missingRoots.Concat(_unreadableDirs).ToList();
                await CleanupOrphansAsync(context, library, knownFilesCache, seenPaths, shieldedPaths, cancellationToken);
                await CleanupEmptyContainersAsync(context, library, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("[{Scanner}] Completed scan. Processed {Count}. New: {New}, Upd: {Upd}, Errors: {Err}",
                DisplayName, processedCount, newCount, updatedCount, errorCount);

            progress?.Report(new ScanProgress(processedCount, totalFiles, null, "Complete",
                newCount, updatedCount, skippedCount, errorCount, LibraryScanStage.Finishing));
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

    // Bound on directory recursion depth. Deep enough for any real media library, but caps a
    // pathological/hostile tree (audit wave-2 L-22).
    private const int MaxScanDepth = 64;

    protected virtual IEnumerable<string> EnumerateDirectories(List<string> libraryPaths)
    {
        foreach (var path in libraryPaths)
        {
            if (!Directory.Exists(path)) continue;

            yield return path; // The root itself

            // Manual DFS instead of Directory.EnumerateDirectories(SearchOption.AllDirectories):
            // (1) skip reparse points (symlinks/junctions) so a hostile symlink dropped inside a
            //     library can't redirect the scan outside it or create an unbounded cycle, and
            // (2) bound the depth. The framework recursive enumerator follows reparse points with
            //     no cycle detection (audit wave-2 L-22).
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((path, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                if (depth >= MaxScanDepth)
                {
                    // Children beyond the cap are unwalked, not gone — shield the subtree.
                    _unreadableDirs.Add(dir);
                    continue;
                }

                List<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch
                {
                    // permission / transient IO — children unknown, shield the subtree
                    _unreadableDirs.Add(dir);
                    continue;
                }

                foreach (var sub in subdirs)
                {
                    bool isReparse;
                    try { isReparse = (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0; }
                    catch { _unreadableDirs.Add(sub); continue; }

                    if (isReparse)
                    {
                        _logger.LogWarning("[{Scanner}] Skipping reparse point (symlink/junction): {Dir}", DisplayName, sub);
                        continue;
                    }

                    yield return sub;
                    stack.Push((sub, depth + 1));
                }
            }
        }
    }

    protected virtual IEnumerable<FileDiscoveryResult> EnumerateFilesCurrentDir(string dirPath)
    {
        var dirInfo = new DirectoryInfo(dirPath);
        if (!dirInfo.Exists) yield break;

        // Materialize inside the try: EnumerateFiles is lazy, so access errors surface
        // during ITERATION — a catch around just the call never sees them and they would
        // otherwise abort the whole scan. A failed listing also shields this directory
        // from orphan cleanup (its files are unknown, not gone).
        List<FileInfo>? files = null;
        try
        {
            files = dirInfo.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            _unreadableDirs.Add(dirPath);
        }
        if (files == null) yield break;

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
             await _metadataQueue.EnqueueMetadataRefreshAsync(opResult.ItemId, library.Type, libraryId: library.Id);
        }

        // No ScanProgress notification here: a watcher-imported file used to emit a
        // "1/1" event that clobbered any live scan toast for the same library. Item
        // and recently-added notifications cover the UI refresh.
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
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;

        // NR-WI-014: Movie/TV companion clips (a "-trailer"/"-sample" suffix, or files in
        // an extras/trailers subfolder) belong to their title's detail page (ExtrasService)
        // — admitting them here would mint junk library items ("Movie-trailer" cards).
        // Previously-admitted companions purge as orphans on the next scan.
        if ((SupportedType == LibraryType.Movie || SupportedType == LibraryType.TV)
            && Constants.MediaCompanions.IsCompanion(filePath))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the library root path is reachable. Overridable so test scanners with a
    /// virtual filesystem can control root availability.
    /// </summary>
    protected virtual bool RootExists(string path) => Directory.Exists(path);

    /// <summary>
    /// Remove items from database whose file no longer exists on disk, computed as the
    /// set difference between the paths known at scan start and the paths discovered this
    /// scan. Containers (Series, Seasons, Artists, Albums, ComicSeries) are excluded —
    /// their Path is a folder, never a discovered file, so they'd always look orphaned.
    /// Items under a shielded path (unreachable root, unlistable directory) are preserved:
    /// their files weren't discoverable, which says nothing about whether they still exist.
    /// </summary>
    protected async Task CleanupOrphansAsync(
        AppDbContext context,
        Library library,
        IReadOnlyDictionary<string, MediaItem> knownItemsByPath,
        IReadOnlySet<string> seenPaths,
        IReadOnlyList<string> shieldedPaths,
        CancellationToken cancellationToken)
    {
        var containerTypes = new[]
        {
            MediaType.Series,
            MediaType.Season,
            MediaType.Artist,
            MediaType.Album,
            MediaType.ComicSeries
        };

        // Separator-normalized, with a trailing separator so "/media/tv" can't shield "/media/tv2".
        static string NormalizeSeparators(string p) => p.Replace('\\', '/');
        var shieldedPrefixes = shieldedPaths
            .Select(r => NormalizeSeparators(r).TrimEnd('/') + '/')
            .ToList();
        bool IsShielded(string itemPath) =>
            shieldedPrefixes.Any(prefix => NormalizeSeparators(itemPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var orphanIds = knownItemsByPath
            .Where(kv => !seenPaths.Contains(kv.Key)
                      && !containerTypes.Contains(kv.Value.Type)
                      && !IsShielded(kv.Key))
            .Select(kv => kv.Value.Id)
            .ToList();

        if (orphanIds.Count == 0) return;

        int deletedCount = 0;

        // ExecuteDeleteAsync is highly performant but unsupported by InMemory test provider
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var orphans = await context.MediaItems
                .Where(m => orphanIds.Contains(m.Id))
                .ToListAsync(cancellationToken);

            if (orphans.Count > 0)
            {
                context.MediaItems.RemoveRange(orphans);
                deletedCount = orphans.Count;
            }
        }
        else
        {
            // Chunked to stay under SQLite's bound-parameter limit
            foreach (var chunk in orphanIds.Chunk(500))
            {
                deletedCount += await context.MediaItems
                    .Where(m => chunk.Contains(m.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
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
