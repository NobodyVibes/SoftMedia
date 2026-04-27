using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataRetryService
{
    Task EnqueueRetryAsync(Guid mediaItemId, LibraryType libraryType, int previousRetries);
}

/// <summary>
/// Persistent metadata retry service backed by the MetadataRetries SQLite table.
/// Survives application restarts unlike the previous ConcurrentQueue implementation.
/// </summary>
public class MetadataRetryService : BackgroundService, IMetadataRetryService
{
    private readonly ILogger<MetadataRetryService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const int MaxRetries = 3;

    // Retry delays: 1 min, 5 min, 30 min, 4 hours
    private static readonly TimeSpan[] BackoffDelays =
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

    /// <summary>
    /// Persist a retry entry to the database with exponential backoff.
    /// </summary>
    public async Task EnqueueRetryAsync(Guid mediaItemId, LibraryType libraryType, int previousRetries)
    {
        if (previousRetries >= MaxRetries)
        {
            _logger.LogWarning("Max retries reached for MediaItem {Id}. Moving to exhausted state.", mediaItemId);
            await MarkAsExhaustedAsync(mediaItemId);
            return;
        }

        var delay = BackoffDelays[Math.Min(previousRetries, BackoffDelays.Length - 1)];
        var retry = new MetadataRetry
        {
            MediaItemId = mediaItemId,
            LibraryType = libraryType,
            RetryCount = previousRetries + 1,
            NextAttempt = DateTime.UtcNow.Add(delay),
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Avoid duplicate entries for the same MediaItem
            var existing = await dbContext.MetadataRetries
                .FirstOrDefaultAsync(r => r.MediaItemId == mediaItemId);

            if (existing != null)
            {
                existing.RetryCount = retry.RetryCount;
                existing.NextAttempt = retry.NextAttempt;
            }
            else
            {
                dbContext.MetadataRetries.Add(retry);
            }

            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Enqueued persistent retry for MediaItem {Id} at {Time} (Attempt {Attempt})",
                mediaItemId, retry.NextAttempt, retry.RetryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist retry entry for MediaItem {Id}", mediaItemId);
        }
    }

    /// <summary>
    /// Background loop: every 30 seconds, query the DB for due retries and re-queue them.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Short startup delay to let the app initialize
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessRetriesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown — host triggered stoppingToken. Exit cleanly
            // rather than propagating, which would abort the whole host startup.
        }
    }

    /// <summary>
    /// Query MetadataRetries for entries where NextAttempt <= now, re-queue them, and delete the rows.
    /// </summary>
    private async Task ProcessRetriesAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queueService = scope.ServiceProvider.GetRequiredService<IMetadataQueue>();

            var now = DateTime.UtcNow;
            var readyItems = await dbContext.MetadataRetries
                .Where(r => r.NextAttempt <= now)
                .ToListAsync();

            if (readyItems.Count == 0)
                return;

            foreach (var item in readyItems)
            {
                _logger.LogInformation("Re-queuing MediaItem {Id} to main metadata queue (Attempt {Attempt})",
                    item.MediaItemId, item.RetryCount);
                await queueService.EnqueueMetadataRefreshAsync(item.MediaItemId, item.LibraryType, retryCount: item.RetryCount);
            }

            // Remove processed entries
            dbContext.MetadataRetries.RemoveRange(readyItems);
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Processed {Count} metadata retries", readyItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing metadata retries.");
        }
    }

    /// <summary>
    /// Mark a MediaItem as retry-exhausted via dedicated column.
    /// </summary>
    private async Task MarkAsExhaustedAsync(Guid mediaItemId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await dbContext.MediaItems.FindAsync(mediaItemId);

            if (item != null)
            {
                item.IsRetryExhausted = true;

                // Also remove any pending retry entry for this item
                var pendingRetry = await dbContext.MetadataRetries
                    .FirstOrDefaultAsync(r => r.MediaItemId == mediaItemId);
                if (pendingRetry != null)
                    dbContext.MetadataRetries.Remove(pendingRetry);

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
