using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Constants;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Smart file watcher that detects new files and waits for them to be fully downloaded
/// before triggering library scans. Uses file stability detection to avoid scanning
/// incomplete downloads.
/// </summary>
public class LibraryWatcher : BackgroundService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<Guid, List<FileSystemWatcher>> _libraryWatchers = new();
    
    // Track pending files with their last known size and timestamp
    private readonly ConcurrentDictionary<string, PendingFile> _pendingFiles = new();
    
    // Libraries that need scanning (deduplicated)
    private readonly ConcurrentDictionary<Guid, DateTime> _librariesToScan = new();
    
    // Configuration
    private const int StabilityCheckIntervalMs = 5000; // Check every 5 seconds
    private const int FileStabilitySeconds = 10; // File must be stable for 10 seconds
    private const int ScanDebounceSeconds = 15; // Wait 15 seconds of no new files before scanning
    private const int LockedFileTimeoutMinutes = 15; // Give up on locked files after 15 minutes
    private const int StalledFileTimeoutMinutes = 15; // Give up on stalled downloads after 15 minutes
    private const int AbsoluteTimeoutHours = 2; // Absolute max wait time

    // Track file watcher issues for admin visibility
    private readonly ConcurrentDictionary<string, FileWatcherIssue> _fileIssues = new();

    // Concurrency control for single-file processing (C5 fix)
    // Limits parallel scope/DbContext creation to prevent SQLite write contention
    private readonly SemaphoreSlim _stableFileSemaphore = new(3, 3);

    // R-WI-007: serialises RefreshWatchersAsync so two concurrent library edits can't
    // race on the watcher collections while tearing down and rebuilding.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // True only while the processing loop is actually running. When EnableFileWatcher is
    // off at startup ExecuteAsync returns before setting this, and RefreshWatchersAsync
    // no-ops — registering watchers with no loop to drain them would silently black-hole
    // events into _pendingFiles. It is also false in unit tests (which never start the
    // host), so RefreshWatchersAsync is safe to call there despite a null scope factory.
    private volatile bool _isRunning;

    /// <summary>Gets current file watcher issues for admin dashboard.</summary>
    public IEnumerable<FileWatcherIssue> GetFileIssues() => _fileIssues.Values.ToList();
    
    /// <summary>Clears a specific file issue.</summary>
    public bool ClearIssue(string path) => _fileIssues.TryRemove(path, out _);
    
    /// <summary>Retries a file by adding it back to pending queue.</summary>
    public bool RetryFile(string path)
    {
        if (!_fileIssues.TryRemove(path, out var issue)) return false;
        if (!File.Exists(path)) return false;
        
        _pendingFiles[path] = new PendingFile
        {
            Path = path,
            LastSize = GetFileSizeSafe(path),
            LastSizeChange = DateTime.UtcNow,
            LibraryId = issue.LibraryId,
            CheckCount = 0,
            FirstSeen = DateTime.UtcNow
        };
        _logger.LogInformation("Retrying file: {Path}", path);
        return true;
    }

    /// <summary>
    /// Removes all file watchers for a specific library.
    /// Called when a library is deleted.
    /// </summary>
    public void RemoveWatchersForLibrary(Guid libraryId)
    {
        lock (_libraryWatchers)
        {
            if (_libraryWatchers.TryGetValue(libraryId, out var watchers))
            {
                foreach (var watcher in watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _watchers.Remove(watcher);
                    _logger.LogInformation("Removed watcher for deleted library {LibraryId}: {Path}", 
                        libraryId, watcher.Path);
                }
                _libraryWatchers.Remove(libraryId);
            }
        }

        // Also clean up any pending files for this library
        var filesToRemove = _pendingFiles
            .Where(kvp => kvp.Value.LibraryId == libraryId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var path in filesToRemove)
        {
            _pendingFiles.TryRemove(path, out _);
        }

        // Remove from scan queue
        _librariesToScan.TryRemove(libraryId, out _);
    }

    private class PendingFile
    {
        public string Path { get; set; } = string.Empty;
        public long LastSize { get; set; }
        public DateTime LastSizeChange { get; set; }
        public DateTime FirstSeen { get; set; }
        public Guid LibraryId { get; set; }
        public int CheckCount { get; set; }
    }

    public LibraryWatcher(IServiceScopeFactory scopeFactory, ILogger<LibraryWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library Watcher starting...");
        
        // Check if file watcher is enabled in settings
        using (var scope = _scopeFactory.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var enabled = await settingsService.GetSettingAsync("EnableFileWatcher", true);
            if (!enabled)
            {
                _logger.LogInformation("FileWatcher disabled by EnableFileWatcher setting. Exiting.");
                return;
            }
        }
        
        // Set running BEFORE the initial registration and route it through the locked
        // RefreshWatchersAsync path, so a library created during the startup window (Kestrel
        // serves requests as soon as ExecuteAsync first awaits) serialises behind this initial
        // registration instead of no-opping and missing its watcher until restart (diff-review LOW).
        _isRunning = true;
        await RefreshWatchersAsync();

        // Main loop - periodically check pending files and trigger scans
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingFilesAsync();
                    await TriggerPendingScansAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in file watcher processing loop");
                }

                await Task.Delay(StabilityCheckIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown — host cancelled. Exit cleanly so the
            // exception does not propagate out of StartAsync/ExecuteAsync
            // and abort the entire host.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Library Watcher...");
        _isRunning = false;

        lock (_libraryWatchers)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _libraryWatchers.Clear();
        }
        _stableFileSemaphore.Dispose();

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Rebuilds the watcher set from the current library configuration. Called by
    /// <c>LibraryService</c> after a library is created or its paths are edited, so
    /// real-time detection covers libraries added while the server is running (R-WI-007) —
    /// previously watchers were registered only once at startup. A full teardown-and-rebuild
    /// is cheap at home scale and also clears watchers left on paths removed during an edit.
    /// No-ops when the processing loop is not running (see <see cref="_isRunning"/>).
    /// </summary>
    public virtual async Task RefreshWatchersAsync()
    {
        if (!_isRunning)
        {
            _logger.LogDebug(
                "RefreshWatchersAsync skipped: the file-watcher loop is not running " +
                "(EnableFileWatcher was off at startup). A restart is required to enable it.");
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            lock (_libraryWatchers)
            {
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _watchers.Clear();
                _libraryWatchers.Clear();
            }

            await InitializeWatchersAsync();
            await PrunePendingFilesAsync();
            _logger.LogInformation("File watchers refreshed after library change.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Drops pending files whose path no longer falls under any current library root, so a
    /// file left pending under a path removed during a library edit is not later scanned in.
    /// (Library deletion already prunes via <see cref="RemoveWatchersForLibrary"/>; this covers
    /// the path-edit case.)
    /// </summary>
    private async Task PrunePendingFilesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roots = (await context.Libraries.ToListAsync())
            .SelectMany(l => l.Paths ?? new List<string>())
            .ToList();

        foreach (var path in _pendingFiles.Keys.ToList())
        {
            if (!roots.Any(root => IsPathUnderRoot(path, root)))
            {
                _pendingFiles.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="filePath"/> is <paramref name="root"/> itself or lives beneath
    /// it. Compares canonical absolute forms with a trailing separator so a sibling whose name is
    /// a prefix (e.g. <c>C:\Media2</c> vs root <c>C:\Media</c>) is not treated as inside. On an
    /// unparseable path it returns <c>true</c> (keep the entry rather than risk over-pruning).
    /// </summary>
    public static bool IsPathUnderRoot(string filePath, string root)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var fullRoot = Path.GetFullPath(root);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(full, fullRoot, comparison)) return true;

            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar) &&
                !fullRoot.EndsWith(Path.AltDirectorySeparatorChar))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }
            return full.StartsWith(fullRoot, comparison);
        }
        catch
        {
            return true;
        }
    }

    private async Task InitializeWatchersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var libraries = await context.Libraries.ToListAsync();

        foreach (var library in libraries)
        {
            foreach (var path in library.Paths)
            {
                if (Directory.Exists(path))
                {
                    CreateWatcher(path, library.Id);
                }
            }
        }
    }

    private void CreateWatcher(string path, Guid libraryId)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                // SR-WI-013: the 8 KB default overflows during large copies (each event is
                // ~var-length; a season drop can exceed it), silently losing events. 64 KB is
                // the documented maximum that stays in the non-paged pool sweet spot.
                InternalBufferSize = 64 * 1024
            };

            // Store libraryId in the watcher's context
            watcher.Created += (sender, e) => OnFileCreated(e.FullPath, libraryId);
            watcher.Changed += (sender, e) => OnFileChanged(e.FullPath, libraryId);
            watcher.Deleted += (sender, e) => OnFileDeleted(e.FullPath, libraryId);
            watcher.Renamed += (sender, e) => OnFileRenamed(e.OldFullPath, e.FullPath, libraryId);
            watcher.Error += (sender, e) => OnError(e, libraryId);

            watcher.EnableRaisingEvents = true;

            // Track watcher in both collections under one lock. _watchers and
            // _libraryWatchers must stay consistent — RefreshWatchersAsync/StopAsync
            // tear both down under this same lock (R-WI-007).
            lock (_libraryWatchers)
            {
                _watchers.Add(watcher);
                if (!_libraryWatchers.ContainsKey(libraryId))
                    _libraryWatchers[libraryId] = new List<FileSystemWatcher>();
                _libraryWatchers[libraryId].Add(watcher);
            }
            _logger.LogInformation("Watching directory: {Path} for library {LibraryId}", path, libraryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for path: {Path}", path);
        }
    }

    private void OnFileCreated(string fullPath, Guid libraryId)
    {
        if (!IsMediaFile(fullPath)) return;
        
        _logger.LogDebug("File created: {Path}", fullPath);
        
        // Clear any previous issue for this file
        _fileIssues.TryRemove(fullPath, out _);
        
        // Add to pending files - will be checked for stability
        _pendingFiles[fullPath] = new PendingFile
        {
            Path = fullPath,
            LastSize = GetFileSizeSafe(fullPath),
            LastSizeChange = DateTime.UtcNow,
            FirstSeen = DateTime.UtcNow,
            LibraryId = libraryId,
            CheckCount = 0
        };
    }

    private void OnFileChanged(string fullPath, Guid libraryId)
    {
        if (!IsMediaFile(fullPath)) return;
        
        // Update the pending file's size and timestamp
        if (_pendingFiles.TryGetValue(fullPath, out var pending))
        {
            var currentSize = GetFileSizeSafe(fullPath);
            if (currentSize != pending.LastSize)
            {
                pending.LastSize = currentSize;
                pending.LastSizeChange = DateTime.UtcNow;
            }
        }
        else
        {
            // File changed but wasn't in pending - add it for checking
            _pendingFiles[fullPath] = new PendingFile
            {
                Path = fullPath,
                LastSize = GetFileSizeSafe(fullPath),
                LastSizeChange = DateTime.UtcNow,
                FirstSeen = DateTime.UtcNow,
                LibraryId = libraryId,
                CheckCount = 0
            };
        }
    }

    private void OnFileDeleted(string fullPath, Guid libraryId)
    {
        // Remove from pending if it was there
        _pendingFiles.TryRemove(fullPath, out _);
        
        // Schedule library scan to clean up orphans
        _librariesToScan[libraryId] = DateTime.UtcNow;
        _logger.LogDebug("Detected file deletion: {Path}", fullPath);
        _logger.LogInformation("File deleted: {Path}", fullPath);
    }

    private void OnFileRenamed(string oldPath, string newPath, Guid libraryId)
    {
        _pendingFiles.TryRemove(oldPath, out _);

        if (IsMediaFile(newPath))
        {
            _pendingFiles[newPath] = new PendingFile
            {
                Path = newPath,
                LastSize = GetFileSizeSafe(newPath),
                LastSizeChange = DateTime.UtcNow,
                FirstSeen = DateTime.UtcNow,
                LibraryId = libraryId,
                CheckCount = 0
            };
        }

        // SR-WI-013: a rename leaves the OLD path's DB row dangling, and a DIRECTORY rename
        // (renaming a show/movie folder — IsMediaFile is false for both paths) used to be
        // ignored entirely, leaving the whole subtree stale until a manual scan. Schedule a
        // library scan either way: its reconciliation pass (SR-WI-012) re-binds the moved
        // files so identity and history survive.
        if (IsMediaFile(oldPath) || Directory.Exists(newPath))
        {
            _librariesToScan[libraryId] = DateTime.UtcNow;
        }

        _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", oldPath, newPath);
    }

    private void OnError(ErrorEventArgs e, Guid libraryId)
    {
        // SR-WI-013: a watcher error (typically InternalBufferSize overflow during a large
        // copy) means events were LOST — new files may never arrive. A scan of the affected
        // library is the only way to recover what was missed; logging alone left the library
        // silently stale.
        _logger.LogError(e.GetException(),
            "FileSystemWatcher error for library {LibraryId}; scheduling a recovery scan (events may have been lost)",
            libraryId);
        _librariesToScan[libraryId] = DateTime.UtcNow;
    }

    private async Task ProcessPendingFilesAsync()
    {
        var now = DateTime.UtcNow;
        var filesToRemove = new List<string>();
        
        foreach (var kvp in _pendingFiles)
        {
            var pending = kvp.Value;
            pending.CheckCount++;
            
            // Check if file still exists
            if (!File.Exists(pending.Path))
            {
                filesToRemove.Add(kvp.Key);
                continue;
            }
            
            // Check current size
            var currentSize = GetFileSizeSafe(pending.Path);
            var totalWaitMinutes = (now - pending.FirstSeen).TotalMinutes;
            var stableMinutes = (now - pending.LastSizeChange).TotalMinutes;
            
            // If size changed, file is still growing - reset stable timer and keep waiting
            if (currentSize != pending.LastSize)
            {
                pending.LastSize = currentSize;
                pending.LastSizeChange = now;
                continue; // Keep waiting as long as file is growing
            }
            
            // Check if file has been stable long enough
            var stableSeconds = (now - pending.LastSizeChange).TotalSeconds;
            if (stableSeconds >= FileStabilitySeconds)
            {
                // File is stable - check if we can open it (not locked)
                if (IsFileReady(pending.Path))
                {
                    _logger.LogInformation("File ready for scanning: {Path} (stable for {Seconds}s)", 
                        pending.Path, stableSeconds);
                    
                    // Process with bounded concurrency instead of fire-and-forget
                    _ = ProcessStableFileWithThrottleAsync(pending.Path, pending.LibraryId);
                    filesToRemove.Add(kvp.Key);
                    continue;
                }
                
                // File stable but locked - check for locked file timeout (15 min)
                if (stableMinutes >= LockedFileTimeoutMinutes)
                {
                    _logger.LogWarning("Giving up on locked file after {Minutes}min: {Path}", 
                        stableMinutes, pending.Path);
                    AddFileIssue(pending, FileWatcherIssueStatus.Locked);
                    _librariesToScan[pending.LibraryId] = now; // Try scan anyway
                    filesToRemove.Add(kvp.Key);
                    continue;
                }
            }
            
            // Check for stalled download (no size change for 15 min)
            if (stableMinutes >= StalledFileTimeoutMinutes && currentSize == pending.LastSize)
            {
                _logger.LogWarning("Giving up on stalled file after {Minutes}min: {Path}", 
                    stableMinutes, pending.Path);
                AddFileIssue(pending, FileWatcherIssueStatus.Stalled);
                _librariesToScan[pending.LibraryId] = now;
                filesToRemove.Add(kvp.Key);
                continue;
            }
            
            // Absolute timeout (2 hours)
            if (totalWaitMinutes >= AbsoluteTimeoutHours * 60)
            {
                _logger.LogWarning("Absolute timeout ({Hours}h) for file: {Path}", 
                    AbsoluteTimeoutHours, pending.Path);
                AddFileIssue(pending, FileWatcherIssueStatus.Timeout);
                _librariesToScan[pending.LibraryId] = now;
                filesToRemove.Add(kvp.Key);
            }
        }
        
        // Remove processed files
        foreach (var path in filesToRemove)
        {
            _pendingFiles.TryRemove(path, out _);
        }
        
        await Task.CompletedTask;
    }
    
    private void AddFileIssue(PendingFile pending, string status)
    {
        _fileIssues[pending.Path] = new FileWatcherIssue
        {
            Path = pending.Path,
            Status = status,
            FirstSeen = pending.FirstSeen,
            LastChecked = DateTime.UtcNow,
            LibraryId = pending.LibraryId,
            CanRetry = true
        };
    }

    private async Task TriggerPendingScansAsync()
    {
        var now = DateTime.UtcNow;
        var librariesToScan = new List<Guid>();
        
        foreach (var kvp in _librariesToScan)
        {
            // Wait for debounce period before scanning
            if ((now - kvp.Value).TotalSeconds >= ScanDebounceSeconds)
            {
                librariesToScan.Add(kvp.Key);
            }
        }
        
        if (librariesToScan.Count == 0) return;
        
        using var scope = _scopeFactory.CreateScope();
        var scanQueueService = scope.ServiceProvider.GetRequiredService<ILibraryScanQueueService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        foreach (var libraryId in librariesToScan)
        {
            _librariesToScan.TryRemove(libraryId, out _);
            
            var library = await context.Libraries.FindAsync(libraryId);
            if (library != null)
            {
                // Check if library is already being scanned
                if (!scanQueueService.IsLibraryInQueue(libraryId))
                {
                    _logger.LogInformation("Triggering scan for library: {Name} (file watcher)", library.Name);
                    scanQueueService.EnqueueScan(libraryId, library.Name);
                }
            }
        }
    }

    private static long GetFileSizeSafe(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsFileReady(string path)
    {
        try
        {
            // Try to open file with exclusive access
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return true; // Other errors = file exists but can't be accessed for other reasons
        }
    }

    /// <summary>
    /// Throttled wrapper around ProcessStableFileAsync.
    /// Limits concurrent file processing to prevent SQLite write contention
    /// when many files stabilize in the same check cycle.
    /// </summary>
    private async Task ProcessStableFileWithThrottleAsync(string filePath, Guid libraryId)
    {
        try
        {
            await _stableFileSemaphore.WaitAsync();
            try
            {
                await ProcessStableFileAsync(filePath, libraryId);
            }
            finally
            {
                _stableFileSemaphore.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Semaphore disposed during shutdown — ignore gracefully
        }
    }

    /// <summary>
    /// Process a single stable file via the scanner orchestrator instead of triggering a full library scan.
    /// </summary>
    private async Task ProcessStableFileAsync(string filePath, Guid libraryId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var scannerOrchestrator = scope.ServiceProvider.GetRequiredService<IScannerOrchestrator>();
            await scannerOrchestrator.ProcessSingleFileAsync(filePath, libraryId);
            _logger.LogInformation("Single-file scan completed: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Single-file scan failed for {Path}, falling back to full library scan", filePath);
            _librariesToScan[libraryId] = DateTime.UtcNow;
        }
    }

    private static readonly HashSet<string> _mediaExtensions =
        new(MediaExtensions.All, StringComparer.OrdinalIgnoreCase);

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return _mediaExtensions.Contains(ext);
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _refreshLock.Dispose();
        base.Dispose();
    }
}
