using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
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
            // 0. Read per-scan settings once (avoids per-file DB lookups)
            int maxPurgePercent;
            int missingRetentionDays;
            using (var settingsScope = _scopeFactory.CreateScope())
            {
                var settingsService = settingsScope.ServiceProvider.GetRequiredService<ISettingsService>();
                var enrichmentMode = await settingsService.GetSettingAsync("MetadataEnrichmentMode", "Relaxed");
                _strictEnrichment = enrichmentMode == "Strict";

                // SR-WI-010/011 data-safety knobs. Unparsable values fall back to the seeds.
                if (!int.TryParse(await settingsService.GetSettingAsync("MaxScanPurgePercent", "25"), out maxPurgePercent))
                    maxPurgePercent = 25;
                maxPurgePercent = Math.Clamp(maxPurgePercent, 1, 100);
                if (!int.TryParse(await settingsService.GetSettingAsync("MissingItemRetentionDays", "30"), out missingRetentionDays))
                    missingRetentionDays = 30;
                missingRetentionDays = Math.Clamp(missingRetentionDays, 0, 3650);
            }

            _unreadableDirs.Clear();
            _scanDirListings.Clear(); // SM-WI-051: listings are per-scan (sidecars may change between scans)

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

            // 1.5. Build an O(1) bulk dictionary lookup to prevent N+1 queries during parallel scan.
            // SM-WI-056: platform comparer — OrdinalIgnoreCase everywhere silently merged
            // case-distinct files on Linux filesystems.
            _logger.LogDebug("[{Scanner}] Bulk-loading existing media items into memory for library '{LibraryName}'", DisplayName, library.Name);
            var knownFilesCache = new ConcurrentDictionary<string, MediaItem>(PathComparers.Platform);
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
                PathComparers.Platform);

            // 2.1. SR-WI-012 — renames/moves: before the processing walk creates brand-new
            //      items for moved files, re-bind DB rows whose path vanished to discovered
            //      files that match by (size, mtime) or uniquely by filename. Identity (and
            //      with it watch history, ratings, playlist membership) survives the move.
            //      Must run BEFORE processing: afterwards the new path would already have a
            //      fresh row and the old row would purge with its children.
            await ReconcileMovedFilesAsync(library, knownFilesCache, seenPaths, discovered,
                missingRoots.Concat(_unreadableDirs).ToList(), cancellationToken);

            // SM-WI-050: the parallel/transaction unit is a BOUNDED BATCH of files, not a
            // directory. With per-directory units, a flat 10k-file folder was ONE work
            // item: zero parallelism, one context tracking 10k entities, and a single
            // save whose failure/cancel discarded the whole library's probe results.
            // Small directories pack together up to the batch size; big directories
            // split into chunks — same-directory files stay adjacent either way, so the
            // striped parent locks and per-batch contexts behave as before.
            var batches = BuildScanBatches(discovered);

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

            await Parallel.ForEachAsync(batches, parallelOptions, async (batch, ct) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // List for deferred metadata enqueueing
                var deferredQueue = new List<(Guid Id, LibraryType Type)>();

                foreach (var fileResult in batch)
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

                            // SR-WI-011 heal-on-reappear: the file is back on disk, so the
                            // soft-delete flag clears and the item (with all its user data)
                            // returns to every surface. Runs before ProcessFileAsync so even
                            // a Skipped (unchanged) result persists the heal.
                            if (existing.IsMissing)
                            {
                                existing.IsMissing = false;
                                existing.MissingSinceUtc = null;
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
                await CleanupOrphansAsync(context, library, knownFilesCache, seenPaths, shieldedPaths,
                    totalFiles, maxPurgePercent, missingRetentionDays, cancellationToken);
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

                List<DirectoryInfo> subdirs;
                try { subdirs = new DirectoryInfo(dir).EnumerateDirectories().ToList(); }
                catch
                {
                    // permission / transient IO — children unknown, shield the subtree
                    _unreadableDirs.Add(dir);
                    continue;
                }

                foreach (var sub in subdirs)
                {
                    // SM-WI-043/S14: DirectoryInfo.Attributes comes from the enumeration's
                    // own data on Windows — the old File.GetAttributes(sub) was one extra
                    // stat round-trip per directory (felt on SMB).
                    bool isReparse;
                    try { isReparse = (sub.Attributes & FileAttributes.ReparsePoint) != 0; }
                    catch { _unreadableDirs.Add(sub.FullName); continue; }

                    if (isReparse)
                    {
                        _logger.LogWarning("[{Scanner}] Skipping reparse point (symlink/junction): {Dir}", DisplayName, sub.FullName);
                        continue;
                    }

                    yield return sub.FullName;
                    stack.Push((sub.FullName, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// SM-WI-050 — flatten discovery output into bounded batches (the parallel/save
    /// unit). Directory locality is preserved: a directory's files are never
    /// interleaved with another's, and big directories split into consecutive chunks.
    /// Public static + pure so tests can assert the batching invariants directly.
    /// </summary>
    public static List<List<FileDiscoveryResult>> BuildScanBatches(
        IReadOnlyList<(string Dir, List<FileDiscoveryResult> Files)> discovered, int batchSize = 100)
    {
        var batches = new List<List<FileDiscoveryResult>>();
        var current = new List<FileDiscoveryResult>();

        foreach (var (_, files) in discovered)
        {
            foreach (var chunk in files.Chunk(batchSize))
            {
                if (current.Count > 0 && current.Count + chunk.Length > batchSize)
                {
                    batches.Add(current);
                    current = new List<FileDiscoveryResult>();
                }

                if (chunk.Length >= batchSize)
                {
                    batches.Add(chunk.ToList());
                }
                else
                {
                    current.AddRange(chunk);
                }
            }
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    // SM-WI-051 — per-scan directory-listing memo. The local-artwork sweep needs each
    // media file's directory listing (sidecar discovery); listing per FILE made flat
    // folders O(N²). One listing per directory per scan; cleared at scan start.
    private readonly ConcurrentDictionary<string, string[]> _scanDirListings = new(PathComparers.Platform);

    /// <summary>
    /// Cached full listing (all extensions — sidecars included) of a directory for the
    /// CURRENT scan. Failures cache an empty array so a broken directory is probed once.
    /// </summary>
    protected string[] GetCachedDirectoryListing(string dirPath)
        => _scanDirListings.GetOrAdd(dirPath, static dir =>
        {
            try { return Directory.GetFiles(dir); }
            catch { return Array.Empty<string>(); }
        });

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
    /// SM-WI-013 — acquire the shared SQLite write gate as a disposable lease. Public so
    /// non-scanner writers on the scan/watch paths (LibraryWatcher's targeted
    /// missing-mark) serialize against scan saves too; previously those saves ran
    /// unlocked and could hit SQLITE_BUSY against a concurrent scan's writers.
    /// </summary>
    public static async Task<IDisposable> AcquireDbWriteLockAsync(CancellationToken ct = default)
    {
        await _dbWriteLock.WaitAsync(ct);
        return new SemaphoreReleaser(_dbWriteLock);
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

        // Case-insensitive to match the full scan's OrdinalIgnoreCase path cache (SR-WI-012):
        // SQLite's default BINARY collation would miss a casing-only rename on Windows and
        // mint a duplicate row that the next scan purges along with its history.
        var lowered = filePath.ToLowerInvariant();
        var existing = await context.MediaItems
            .FirstOrDefaultAsync(m => m.Path != null && m.Path.ToLower() == lowered && m.LibraryId == library.Id,
                cancellationToken);

        // SR-WI-011 heal-on-reappear (watcher path): the file is back, restore the item.
        if (existing is { IsMissing: true })
        {
            existing.IsMissing = false;
            existing.MissingSinceUtc = null;
        }

        var fileInfo = new FileInfo(filePath);
        var fileResult = new FileDiscoveryResult(fileInfo.FullName, fileInfo.Exists ? fileInfo.Length : 0, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);

        var opResult = await ProcessFileAsync(context, fileResult, existing, library, cancellationToken);
        // SM-WI-013: same SR-WI-035 discipline as the full-scan save windows — the
        // watcher allows up to 3 concurrent single-file imports, and a scan may be
        // writing at the same time.
        using (await AcquireDbWriteLockAsync(cancellationToken))
        {
            // SM-WI-061 — close the check-then-add race: another writer (a running
            // scan's batch, or a concurrent watcher import of the same file) may have
            // committed a row for this path between our lookup above and this lock.
            // Re-check INSIDE the lock; if a row exists now, our Added entities are
            // redundant — drop them instead of inserting a duplicate twin (whose loser
            // the next scan would purge along with its user data). The remaining
            // written-after-this-check window is backstopped by the partial unique
            // Path index, which fails the later insert loudly instead of silently.
            if (existing == null && opResult.Result == ScanResult.New)
            {
                var raceWinner = await context.MediaItems.AsNoTracking()
                    .AnyAsync(m => m.Path != null && m.Path.ToLower() == lowered && m.LibraryId == library.Id,
                        cancellationToken);
                if (raceWinner)
                {
                    _logger.LogInformation(
                        "[{Scanner}] {Path} was imported by a concurrent writer while this single-file import ran; discarding the duplicate",
                        DisplayName, filePath);
                    foreach (var entry in context.ChangeTracker.Entries()
                                 .Where(e => e.State == EntityState.Added).ToList())
                    {
                        entry.State = EntityState.Detached;
                    }
                    return;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
        }

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
            // SR-WI-038: surface the skip on the admin file-issues dashboard — a log line
            // alone meant the file just silently never appeared in the library. Rare path
            // (unsafe names), so the scope resolution here is not a hot-path cost.
            try
            {
                using var scope = _scopeFactory.CreateScope();
                scope.ServiceProvider.GetService<LibraryWatcher>()?.ReportUnsafeFilename(filePath);
            }
            catch { /* best-effort reporting only */ }
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

    /// <summary>Container types whose Path is a folder, never a discovered file — always
    /// excluded from orphan handling (they'd otherwise look orphaned on every scan).</summary>
    private static readonly MediaType[] ContainerTypes =
    {
        MediaType.Series,
        MediaType.Season,
        MediaType.Artist,
        MediaType.Album,
        MediaType.ComicSeries
    };

    /// <summary>Minimum newly-missing count before the purge brake can trip (SR-WI-010).
    /// Below this, even a 100% wipe of a tiny library proceeds — a 3-item library losing
    /// 2 files is routine housekeeping, not a mount failure.</summary>
    protected const int PurgeBrakeMinItems = 20;

    /// <summary>
    /// Separator-normalized prefix check with a trailing separator so "/media/tv" can't
    /// shield "/media/tv2". Shared by reconciliation and orphan handling.
    /// </summary>
    private static Func<string, bool> BuildShieldChecker(IReadOnlyList<string> shieldedPaths)
    {
        static string NormalizeSeparators(string p) => p.Replace('\\', '/');
        var prefixes = shieldedPaths
            .Select(r => NormalizeSeparators(r).TrimEnd('/') + '/')
            .ToList();
        return itemPath => prefixes.Any(prefix =>
            NormalizeSeparators(itemPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SR-WI-012 — re-bind DB rows whose file vanished to newly discovered files that are
    /// the same file moved/renamed, so item identity (watch history, ratings, playlist
    /// membership) survives. Two passes over the (unseen-known × unknown-discovered) sets:
    /// (1) unique (size, mtime-to-the-second) match — moves and renames preserve both;
    /// (2) unique filename match — covers files whose content was touched in transit.
    /// Both passes require uniqueness on BOTH sides: an ambiguous match binds nothing.
    /// </summary>
    protected async Task ReconcileMovedFilesAsync(
        Library library,
        ConcurrentDictionary<string, MediaItem> knownFilesCache,
        IReadOnlySet<string> seenPaths,
        IReadOnlyList<(string Dir, List<FileDiscoveryResult> Files)> discovered,
        IReadOnlyList<string> shieldedPaths,
        CancellationToken cancellationToken)
    {
        var isShielded = BuildShieldChecker(shieldedPaths);

        var orphanCandidates = knownFilesCache
            .Where(kv => !seenPaths.Contains(kv.Key)
                      && !ContainerTypes.Contains(kv.Value.Type)
                      && !isShielded(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        if (orphanCandidates.Count == 0) return;

        var newFiles = discovered
            .SelectMany(d => d.Files)
            .Where(f => !knownFilesCache.ContainsKey(f.Path))
            .ToList();
        if (newFiles.Count == 0) return;

        // mtime quantized to whole seconds: SQLite round-trips full precision, but copies
        // across filesystems can truncate sub-second precision.
        static (long Size, long MtimeSeconds) SizeTimeKey(long size, DateTime mtimeUtc) =>
            (size, mtimeUtc.Ticks / TimeSpan.TicksPerSecond);

        var bindings = new List<(MediaItem Item, FileDiscoveryResult File)>();
        var boundItemIds = new HashSet<Guid>();
        var boundFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: unique (size, mtime) on both sides. Zero-byte files carry no signal.
        var orphansByKey = orphanCandidates
            .Where(o => o.Size > 0)
            .GroupBy(o => SizeTimeKey(o.Size, o.DateModified))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var group in newFiles.Where(f => f.Size > 0).GroupBy(f => SizeTimeKey(f.Size, f.LastWriteUtc)))
        {
            if (group.Count() != 1) continue;
            if (!orphansByKey.TryGetValue(group.Key, out var orphan)) continue;
            var file = group.First();
            bindings.Add((orphan, file));
            boundItemIds.Add(orphan.Id);
            boundFilePaths.Add(file.Path);
        }

        // Pass 2: unique filename on both sides, among what pass 1 left unbound.
        var orphansByName = orphanCandidates
            .Where(o => !boundItemIds.Contains(o.Id))
            .GroupBy(o => System.IO.Path.GetFileName(o.Path), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in newFiles
                     .Where(f => !boundFilePaths.Contains(f.Path))
                     .GroupBy(f => System.IO.Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1) continue;
            if (!orphansByName.TryGetValue(group.Key, out var orphan)) continue;
            bindings.Add((orphan, group.First()));
        }

        if (bindings.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int rebound = 0;
        foreach (var (item, file) in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tracked = await context.MediaItems.FirstOrDefaultAsync(m => m.Id == item.Id, cancellationToken);
            if (tracked == null) continue;

            _logger.LogInformation("[{Scanner}] Re-bound moved/renamed file: '{Old}' -> '{New}'",
                DisplayName, tracked.Path, file.Path);

            tracked.Path = file.Path;
            tracked.Size = file.Size;
            tracked.DateModified = file.LastWriteUtc;
            tracked.IsMissing = false;
            tracked.MissingSinceUtc = null;
            tracked.LastScannedUtc = DateTime.UtcNow;

            // Mirror onto the cached snapshot under its new key so the processing walk
            // sees an existing, up-to-date item at the new path (no duplicate row).
            knownFilesCache.TryRemove(item.Path, out _);
            item.Path = file.Path;
            item.Size = file.Size;
            item.DateModified = file.LastWriteUtc;
            item.IsMissing = false;
            item.MissingSinceUtc = null;
            knownFilesCache[file.Path] = item;
            rebound++;
        }

        await _dbWriteLock.WaitAsync(cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _dbWriteLock.Release();
        }

        _logger.LogInformation("[{Scanner}] Re-bound {Count} moved/renamed files (identity and history preserved)",
            DisplayName, rebound);
    }

    /// <summary>
    /// Handle items whose file no longer exists on disk, computed as the set difference
    /// between the paths known at scan start and the paths discovered this scan.
    /// SR-WI-010/011: instead of hard-deleting (which cascades away play history,
    /// interactions, bookmarks and playlist rows), items are soft-deleted (IsMissing) and
    /// only hard-deleted after the retention window. A purge brake refuses to mark
    /// anything when an implausible fraction of the library vanishes at once — the usual
    /// cause is an unmounted drive or a share that reconnected empty, not real deletions.
    /// Items under a shielded path (unreachable root, unlistable directory) are preserved:
    /// their files weren't discoverable, which says nothing about whether they still exist.
    /// </summary>
    protected async Task CleanupOrphansAsync(
        AppDbContext context,
        Library library,
        IReadOnlyDictionary<string, MediaItem> knownItemsByPath,
        IReadOnlySet<string> seenPaths,
        IReadOnlyList<string> shieldedPaths,
        int discoveredFileCount,
        int maxPurgePercent,
        int missingRetentionDays,
        CancellationToken cancellationToken)
    {
        var isShielded = BuildShieldChecker(shieldedPaths);
        bool isInMemory = context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";

        var orphans = knownItemsByPath
            .Where(kv => !seenPaths.Contains(kv.Key)
                      && !ContainerTypes.Contains(kv.Value.Type)
                      && !isShielded(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        var eligibleKnown = knownItemsByPath.Values.Count(v => !ContainerTypes.Contains(v.Type));
        var newlyMissing = orphans.Where(o => !o.IsMissing).ToList();

        // SR-WI-010 — purge brake. Trips on (a) a previously non-empty library whose scan
        // discovered zero files (mount point exists but is empty — the classic reconnected
        // empty share), or (b) an implausible newly-missing fraction. When tripped, NOTHING
        // is marked or deleted this scan; the admin is alerted and can either fix the mount
        // and rescan, or raise MaxScanPurgePercent to 100 for one intentional mass removal.
        string? brakeReason = null;
        if (maxPurgePercent < 100 && discoveredFileCount == 0 && eligibleKnown > 0)
        {
            brakeReason = $"the scan discovered 0 files but the library has {eligibleKnown} known items " +
                          "(is the drive or network share mounted?)";
        }
        else if (maxPurgePercent < 100
                 && newlyMissing.Count >= PurgeBrakeMinItems
                 && eligibleKnown > 0
                 && (long)newlyMissing.Count * 100 >= (long)eligibleKnown * maxPurgePercent)
        {
            brakeReason = $"{newlyMissing.Count} of {eligibleKnown} items vanished at once, exceeding the " +
                          $"{maxPurgePercent}% safety threshold (is a drive or share unavailable?)";
        }

        if (brakeReason != null)
        {
            _logger.LogError(
                "[{Scanner}] PURGE BRAKE: library '{Library}' cleanup aborted — {Reason}. " +
                "No items were marked missing or deleted. Fix the storage and rescan, or set " +
                "Scanning > MaxScanPurgePercent to 100 to override once intentionally.",
                DisplayName, library.Name, brakeReason);
            await NotifyPurgeBrakeAsync(library, brakeReason, cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;

        // SR-WI-011 — soft-delete newly missing items (or hard-delete immediately when
        // retention is 0 = legacy behavior).
        if (newlyMissing.Count > 0)
        {
            if (missingRetentionDays == 0)
            {
                await HardDeleteItemsAsync(context, newlyMissing.Select(o => o.Id).ToList(), isInMemory, cancellationToken);
                _logger.LogInformation("[{Scanner}] Removed {Count} orphans (retention disabled)",
                    DisplayName, newlyMissing.Count);
            }
            else
            {
                var newlyMissingIds = newlyMissing.Select(o => o.Id).ToList();
                if (isInMemory)
                {
                    var items = await context.MediaItems
                        .Where(m => newlyMissingIds.Contains(m.Id))
                        .ToListAsync(cancellationToken);
                    foreach (var item in items)
                    {
                        item.IsMissing = true;
                        item.MissingSinceUtc = now;
                    }
                }
                else
                {
                    foreach (var chunk in newlyMissingIds.Chunk(500))
                    {
                        await context.MediaItems
                            .Where(m => chunk.Contains(m.Id))
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(m => m.IsMissing, true)
                                .SetProperty(m => m.MissingSinceUtc, now), cancellationToken);
                    }
                }
                _logger.LogInformation(
                    "[{Scanner}] Marked {Count} items missing (files gone from disk); history retained for {Days} days",
                    DisplayName, newlyMissing.Count, missingRetentionDays);
            }
        }

        // Retention: hard-delete items that have stayed missing past the window. Shielded
        // paths are skipped — an unreadable subtree must not age its items out.
        var cutoff = now.AddDays(-missingRetentionDays);
        var expiredCandidates = await context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == library.Id
                     && m.IsMissing
                     && m.MissingSinceUtc != null
                     && m.MissingSinceUtc < cutoff)
            .Select(m => new { m.Id, m.Path })
            .ToListAsync(cancellationToken);
        var expiredIds = expiredCandidates
            .Where(c => !isShielded(c.Path))
            .Select(c => c.Id)
            .ToList();
        if (expiredIds.Count > 0)
        {
            await HardDeleteItemsAsync(context, expiredIds, isInMemory, cancellationToken);
            _logger.LogInformation(
                "[{Scanner}] Permanently removed {Count} items missing longer than {Days} days",
                DisplayName, expiredIds.Count, missingRetentionDays);
        }
    }

    /// <summary>
    /// SR-WI-010 — surface the purge brake on the admin notification bell (DB-backed
    /// SystemNotifications). Deduped per library so a stuck mount doesn't spam a new
    /// alert on every scheduled scan. Best-effort: an alert failure must not fail the scan.
    /// </summary>
    private async Task NotifyPurgeBrakeAsync(Library library, string reason, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var type = $"scan_purge_brake:{library.Id}";
            if (await notifications.HasActiveOfTypeAsync(type)) return;
            await notifications.CreateAsync(
                type,
                "Library cleanup aborted for safety",
                $"Scanning '{library.Name}' stopped before removing anything: {reason}. " +
                "Nothing was deleted and watch history is intact. If the storage is fine and the " +
                "removals were intentional, set Settings > Scanning > MaxScanPurgePercent to 100 and rescan.",
                "error");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Scanner}] Failed to raise purge-brake notification", DisplayName);
        }
    }

    private async Task HardDeleteItemsAsync(
        AppDbContext context, IReadOnlyList<Guid> ids, bool isInMemory, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;

        // MC-WI-004: capture identity BEFORE the rows go away — subtitle cache keys
        // derive from Path, and artwork/trickplay cleanup needs (Id, Type).
        var doomed = new List<(Guid Id, MediaType Type, string? Path)>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await context.MediaItems
                .AsNoTracking()
                .Where(m => chunk.Contains(m.Id))
                .Select(m => new { m.Id, m.Type, m.Path })
                .ToListAsync(cancellationToken);
            doomed.AddRange(rows.Select(r => (r.Id, r.Type, (string?)r.Path)));
        }

        // ExecuteDeleteAsync is highly performant but unsupported by the InMemory test provider
        if (isInMemory)
        {
            var items = await context.MediaItems
                .Where(m => ids.Contains(m.Id))
                .ToListAsync(cancellationToken);
            context.MediaItems.RemoveRange(items);
        }
        else
        {
            // Chunked to stay under SQLite's bound-parameter limit
            foreach (var chunk in ids.Chunk(500))
            {
                await context.MediaItems
                    .Where(m => chunk.Contains(m.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        // MC-WI-004: reclaim derived artifacts (artwork, trickplay, thumbnails, cached
        // subtitle VTTs) immediately instead of waiting up to a day for the orphan sweep,
        // matching the manual library-delete path. Best-effort — the rows are gone either
        // way and the daily sweep remains the backstop. GetService (not Required): some
        // scanner test hosts don't register the cleanup service.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider
                .GetService<Media.ILibraryCleanupService>()
                ?.DeleteArtifactsForItems(doomed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Scanner}] Artifact cleanup after hard-deleting {Count} item(s) failed; the daily cache sweep will reclaim them",
                DisplayName, doomed.Count);
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
