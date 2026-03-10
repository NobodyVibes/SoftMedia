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
        
        await InitializeWatchersAsync();

        // Main loop - periodically check pending files and trigger scans
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Library Watcher...");
        
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        
        await base.StopAsync(cancellationToken);
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };

            // Store libraryId in the watcher's context
            watcher.Created += (sender, e) => OnFileCreated(e.FullPath, libraryId);
            watcher.Changed += (sender, e) => OnFileChanged(e.FullPath, libraryId);
            watcher.Deleted += (sender, e) => OnFileDeleted(e.FullPath, libraryId);
            watcher.Renamed += (sender, e) => OnFileRenamed(e.OldFullPath, e.FullPath, libraryId);
            watcher.Error += OnError;

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);

            // Track watcher by library ID for cleanup
            lock (_libraryWatchers)
            {
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
        
        _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", oldPath, newPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error");
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
                    
                    // Use single-file processing for individual stable files
                    _ = ProcessStableFileAsync(pending.Path, pending.LibraryId);
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
        base.Dispose();
    }
}
