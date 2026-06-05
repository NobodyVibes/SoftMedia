using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Background service that periodically refreshes metadata for ongoing (Running) TV series.
/// Can be triggered manually via TriggerRefreshNow() or runs on a configurable interval.
/// </summary>

public class MetadataRefreshService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MetadataRefreshService> _logger;
    private readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(1); // Short delay to check startup setting
    private TaskCompletionSource<bool>? _manualTrigger;

    public MetadataRefreshService(IServiceProvider services, ILogger<MetadataRefreshService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Triggers an immediate metadata refresh by enqueuing a job.
    /// </summary>
    public void TriggerRefreshNow()
    {
        using var scope = _services.CreateScope();
        var queueService = scope.ServiceProvider.GetRequiredService<ILibraryScanQueueService>();
        queueService.EnqueueMetadataRefresh();
        _logger.LogInformation("Manual metadata refresh enqueued");

        scope.ServiceProvider.GetRequiredService<IScheduledTaskRegistry>()
            .Report(ScheduledTaskNames.MetadataRefresh, "Success");

        // Signal the loop in case it's waiting on a long interval
        _manualTrigger?.TrySetResult(true);
    }

    /// <summary>
    /// Executed by LibraryScanQueueService when processing a MetadataRefresh job
    /// </summary>
    public async Task RunRefreshJobAsync(LibraryScanJob job, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IMetadataQueue>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var queueService = scope.ServiceProvider.GetRequiredService<ILibraryScanQueueService>();

        // Get Mode
        var modeSetting = await settings.GetSettingAsync("MetadataRefreshMode");
        var mode = modeSetting?.Value ?? "Running";
        
        _logger.LogInformation("Starting Metadata Refresh Job. Mode: {Mode}", mode);

        // Update Job Status
        queueService.UpdateProgress(job.Id, LibraryScanStage.Discovery, 0, 0);

        List<MediaItem> candidates = new();

        if (string.Equals(mode, "Running", StringComparison.OrdinalIgnoreCase))
        {
             // Since status was previously stored in raw payloads, and we've promoted critical fields,
             // refresh all Series items. The TVMaze provider re-fetches full details anyway,
             // so the cost is minimal and this ensures no running series are missed.
             candidates = await context.MediaItems
                .Where(m => m.Type == MediaType.Series)
                .ToListAsync(ct);
        }
        else if (string.Equals(mode, "Variable", StringComparison.OrdinalIgnoreCase) || 
                 string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
        {
             candidates = await context.MediaItems
                .Where(m => (m.Type == MediaType.Series || m.Type == MediaType.Movie))
                .ToListAsync(ct);
        }

        _logger.LogInformation("Found {Count} candidates for metadata refresh", candidates.Count);
        
        // Update Job with Total Files
        queueService.UpdateProgress(job.Id, LibraryScanStage.Processing, 0, candidates.Count);

        bool refreshImages = !string.Equals(mode, "Variable", StringComparison.OrdinalIgnoreCase);

        int enqueuedCount = 0;
        int failCount = 0;
        int processed = 0;

        foreach (var item in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var libType = item.Type == MediaType.Movie ? LibraryType.Movie : LibraryType.TV;
                await queue.EnqueueMetadataRefreshAsync(item.Id, libType, refreshImages);
                enqueuedCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                _logger.LogWarning(ex, "Failed to enqueue refresh for: {Title}", item.Title);
            }

            processed++;
            // Update progress every 50 items or on completion
            if (processed % 50 == 0 || processed == candidates.Count)
            {
                queueService.UpdateProgress(job.Id, LibraryScanStage.Processing, processed, candidates.Count, item.Title);
            }
        }

        // Job is "Complete" when items are queued. Processing happens in background.
        queueService.CompleteJob(job.Id, 0, enqueuedCount, 0, failCount);
        _logger.LogInformation("Metadata refresh job complete: {Enqueued} enqueued, {Failed} failed", enqueuedCount, failCount);

        // Report actual completion (TriggerRefreshNow only reports the enqueue). The
        // per-item enrichment then proceeds via the metadata queue.
        scope.ServiceProvider.GetRequiredService<IScheduledTaskRegistry>()
            .Report(ScheduledTaskNames.MetadataRefresh, failCount > 0 ? "Failed" : "Success");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MetadataRefreshService starting (Scheduler Mode)...");

        // The ManualTrigger is now only used to wake up the scheduler if the user changes the interval
        // NOT for running the scan itself (that goes through the queue)
        _manualTrigger = new TaskCompletionSource<bool>();

        // Initial startup check
        bool runOnStartup = await GetStartupSettingAsync();
        if (runOnStartup)
        {
            _logger.LogInformation("Startup refresh enabled. Enqueuing initial refresh...");
            TriggerRefreshNow();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalDays = await GetIntervalDaysAsync();
            
            if (intervalDays <= 0)
            {
                _logger.LogInformation("Metadata refresh scheduler disabled.");
                // Wait indefinitely until cancelled or manually woken up (e.g. config change)
                try { await Task.WhenAny(_manualTrigger.Task, Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)); }
                catch (OperationCanceledException) { break; }
            }
            else
            {
                _logger.LogInformation("Next scheduled metadata refresh in {Days} days", intervalDays);
                try 
                { 
                    await Task.WhenAny(_manualTrigger.Task, Task.Delay(TimeSpan.FromDays(intervalDays), stoppingToken)); 
                } 
                catch (OperationCanceledException) { break; }
            }
            
            if (stoppingToken.IsCancellationRequested) break;

            // If we woke up due to timeout (schedule), trigger a refresh
            // If woke up due to manual trigger, we just loop around (TriggerRefreshNow already enqueued it)
            if (_manualTrigger.Task.IsCompleted)
            {
                // Reset trigger
                _manualTrigger = new TaskCompletionSource<bool>();
            }
            else
            {
                // Timeout fired -> Enqueue scheduled refresh
                TriggerRefreshNow();
            }
        }
    }
    private async Task<int> GetIntervalDaysAsync()
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var setting = await settings.GetSettingAsync("MetadataRefreshIntervalDays");
        return int.TryParse(setting?.Value, out var days) ? days : 30; // Default 30 days
    }

    private async Task<bool> GetStartupSettingAsync()
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var setting = await settings.GetSettingAsync("MetadataRefreshOnStartup");
        return bool.TryParse(setting?.Value, out var val) && val;
    }
}
