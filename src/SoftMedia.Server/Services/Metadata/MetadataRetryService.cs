using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata;

public class RetryItem
{
    public Guid MediaItemId { get; set; }
    public LibraryType LibraryType { get; set; }
    public int RetryCount { get; set; }
    public DateTime NextAttempt { get; set; }
}

public interface IMetadataRetryService
{
    void EnqueueRetry(Guid mediaItemId, LibraryType libraryType, int previousRetries);
}

public class MetadataRetryService : BackgroundService, IMetadataRetryService
{
    private readonly ConcurrentQueue<RetryItem> _retryQueue = new();
    private readonly ILogger<MetadataRetryService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly int _maxRetries = 3;

    // Retry delays: 1 min, 5 min, 30 min, 4 hours
    private readonly TimeSpan[] _backoffDelays = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(4)
    };

    public MetadataRetryService(ILogger<MetadataRetryService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public void EnqueueRetry(Guid mediaItemId, LibraryType libraryType, int previousRetries)
    {
        if (previousRetries >= _maxRetries)
        {
            _logger.LogWarning("Max retries reached for MediaItem {Id}. Moving to exhausted state.", mediaItemId);
            _ = MarkAsExhaustedAsync(mediaItemId, libraryType);
            return;
        }

        var delay = _backoffDelays[Math.Min(previousRetries, _backoffDelays.Length - 1)];
        var retryItem = new RetryItem
        {
            MediaItemId = mediaItemId,
            LibraryType = libraryType,
            RetryCount = previousRetries + 1,
            NextAttempt = DateTime.UtcNow.Add(delay)
        };

        _retryQueue.Enqueue(retryItem);
        _logger.LogInformation("Enqueued retry for MediaItem {Id} at {Time} (Attempt {Attempt})", 
            mediaItemId, retryItem.NextAttempt, retryItem.RetryCount);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            ProcessRetries();
        }
    }

    private void ProcessRetries()
    {
        var now = DateTime.UtcNow;
        var pending = new List<RetryItem>();
        var ready = new List<RetryItem>();

        // Dequeue everything and sift
        while (_retryQueue.TryDequeue(out var item))
        {
            if (now >= item.NextAttempt)
            {
                ready.Add(item);
            }
            else
            {
                pending.Add(item);
            }
        }

        // Put pending back
        foreach (var p in pending)
        {
            _retryQueue.Enqueue(p);
        }

        // Requeue ready items to MetadataQueueService
        if (ready.Count > 0)
        {
            _ = RequeueItemsAsync(ready);
        }
    }

    private async Task RequeueItemsAsync(List<RetryItem> readyItems)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IMetadataQueue>();

            foreach (var item in readyItems)
            {
                _logger.LogInformation("Re-queuing MediaItem {Id} to main metadata queue (Attempt {Attempt})", 
                    item.MediaItemId, item.RetryCount);
                await queueService.EnqueueMetadataRefreshAsync(item.MediaItemId, item.LibraryType, retryCount: item.RetryCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-queuing retry items.");
        }
    }

    private async Task MarkAsExhaustedAsync(Guid mediaItemId, LibraryType libraryType)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await dbContext.MediaItems.FindAsync(mediaItemId);

            if (item != null)
            {
                // Parse existing JSON
                Dictionary<string, object> metadataDict = new();
                if (!string.IsNullOrEmpty(item.MetadataJson))
                {
                    try
                    {
                        var extracted = JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
                        if (extracted != null) metadataDict = extracted;
                    }
                    catch { /* Ignore parsing errors */ }
                }

                metadataDict["retryExhausted"] = true;
                item.MetadataJson = JsonSerializer.Serialize(metadataDict);
                
                await dbContext.SaveChangesAsync();
                _logger.LogInformation("Marked MediaItem {Id} as retry-exhausted.", mediaItemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking MediaItem {Id} as exhausted.", mediaItemId);
        }
    }
}
