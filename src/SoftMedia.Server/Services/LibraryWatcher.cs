using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Services;

public class LibraryWatcher : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly IFileScannerService _scannerService;

    public LibraryWatcher(IServiceScopeFactory scopeFactory, ILogger<LibraryWatcher> logger, IFileScannerService scannerService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _scannerService = scannerService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Library Watcher...");
        await InitializeWatchersAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Library Watcher...");
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        return Task.CompletedTask;
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
                    var watcher = new FileSystemWatcher(path);
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                    
                    // Add event handlers
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;

                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _logger.LogInformation($"Watching directory: {path}");
                }
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation($"File changed: {e.ChangeType} - {e.FullPath}");
        // In a real app, we would be more granular. For now, trigger a scan of the library containing this file.
        // Optimization: Find which library this file belongs to and scan only that.
        // For simplicity in this phase, we'll trigger a full scan (debouncing would be good here).
        _scannerService.ScanAllLibrariesAsync().GetAwaiter().GetResult(); 
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation($"File renamed: {e.OldFullPath} to {e.FullPath}");
        _scannerService.ScanAllLibrariesAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}
