using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services;

/// <summary>
/// Background service that periodically refreshes metadata for ongoing (Running) TV series.
/// Can be triggered manually via TriggerRefreshNow() or runs on a configurable interval.
/// </summary>
public class MetadataRefreshService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MetadataRefreshService> _logger;
    private readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(5);
    private TaskCompletionSource<bool>? _manualTrigger;

    public MetadataRefreshService(IServiceProvider services, ILogger<MetadataRefreshService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Triggers an immediate metadata refresh for ongoing series.
    /// </summary>
    public void TriggerRefreshNow()
    {
        _logger.LogInformation("Manual metadata refresh triggered");
        _manualTrigger?.TrySetResult(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait initial delay to avoid startup load
        _logger.LogInformation("MetadataRefreshService starting, waiting {Delay} before first run", _initialDelay);
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshRunningSeriesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metadata refresh failed");
            }

            // Wait for interval OR manual trigger
            var intervalHours = await GetIntervalFromSettingsAsync();
            if (intervalHours <= 0)
            {
                // Disabled - wait for manual trigger only
                _logger.LogInformation("Metadata refresh interval disabled, waiting for manual trigger");
                _manualTrigger = new TaskCompletionSource<bool>();
                try
                {
                    await Task.WhenAny(_manualTrigger.Task, Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            else
            {
                _logger.LogInformation("Next metadata refresh in {Hours} hours", intervalHours);
                _manualTrigger = new TaskCompletionSource<bool>();
                try
                {
                    await Task.WhenAny(
                        _manualTrigger.Task,
                        Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken)
                    );
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> GetIntervalFromSettingsAsync()
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var setting = await settings.GetSettingAsync("MetadataRefreshIntervalHours");
        return int.TryParse(setting?.Value, out var hours) ? hours : 24;
    }

    private async Task RefreshRunningSeriesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var aggregator = scope.ServiceProvider.GetRequiredService<MetadataAggregator>();

        // Find all series with status "Running" in their metadata
        var runningSeries = await context.MediaItems
            .Where(m => m.Type == MediaType.Series &&
                        m.MetadataJson != null &&
                        m.MetadataJson.Contains("\"status\":\"Running\""))
            .ToListAsync(ct);

        _logger.LogInformation("Refreshing metadata for {Count} running series", runningSeries.Count);

        int successCount = 0;
        int failCount = 0;

        foreach (var series in runningSeries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await aggregator.EnrichMediaItemAsync(series, LibraryType.TV);
                successCount++;
                _logger.LogDebug("Refreshed: {Title}", series.Title);
            }
            catch (Exception ex)
            {
                failCount++;
                _logger.LogWarning(ex, "Failed to refresh metadata for: {Title}", series.Title);
            }
        }

        await context.SaveChangesAsync(ct);
        _logger.LogInformation("Metadata refresh complete: {Success} succeeded, {Failed} failed", successCount, failCount);
    }
}
