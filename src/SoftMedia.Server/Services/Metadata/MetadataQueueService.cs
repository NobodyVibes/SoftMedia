using System.Threading;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Models;
using SoftMedia.Server.Data; 
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Services.Media; // For ILibraryService
using System.Collections.Concurrent;

namespace SoftMedia.Server.Services.Metadata;

public class MetadataQueueService : BackgroundService, IMetadataQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaNotificationService _notificationService;
    private readonly ILogger<MetadataQueueService> _logger;
    private readonly IScheduledTaskRegistry? _registry;

    // Throttle for the Background Tasks heartbeat: this queue processes items in bursts
    // (many per second during a scan), so we surface "last active" at most this often
    // rather than reporting per item.
    private static readonly long ReportThrottleTicks = TimeSpan.FromSeconds(15).Ticks;
    private long _lastReportTicks;

    // Dynamic Channel Router
    // Key: Channel Name (Music, TV, Shared)
    private readonly Dictionary<string, Channel<MetadataQueueItem>> _channels;
    
    // Event-driven channel for cache updates
    private readonly Channel<Guid> _cacheUpdateChannel;

    // Deduplication guard — prevents the same media item from being enqueued multiple times
    private readonly ConcurrentDictionary<Guid, byte> _pendingIds = new();

    public MetadataQueueService(
        IServiceScopeFactory scopeFactory,
        IMediaNotificationService notificationService,
        ILogger<MetadataQueueService> logger,
        IScheduledTaskRegistry? registry = null)
    {
        _scopeFactory = scopeFactory;
        _notificationService = notificationService;
        _logger = logger;
        _registry = registry;

        // Initialize Channels with appropriate capacities
        // Music: High capacity because scans are large, but processing is slow.
        // TV/Shared: Moderate capacity.
        _channels = new Dictionary<string, Channel<MetadataQueueItem>>
        {
            { "Music", Channel.CreateUnbounded<MetadataQueueItem>() },
            { "TV", Channel.CreateUnbounded<MetadataQueueItem>() },
            { "Shared", Channel.CreateUnbounded<MetadataQueueItem>() }
        };

        _cacheUpdateChannel = Channel.CreateUnbounded<Guid>();
        
    }
    
    private async Task CacheUpdateLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Recently Added Cache Update Loop started (Debounced).");
        var batch = new HashSet<Guid>();
        
        try
        {
            while (await _cacheUpdateChannel.Reader.WaitToReadAsync(ct))
            {
                // Collect all currently available items
                while (_cacheUpdateChannel.Reader.TryRead(out var libId))
                {
                    batch.Add(libId);
                }

                if (batch.Count == 0) continue;

                // Debounce: Wait for more items to arrive during the storm
                await Task.Delay(2000, ct);
                
                // Read again after delay
                while (_cacheUpdateChannel.Reader.TryRead(out var libId))
                {
                    batch.Add(libId);
                }

                _logger.LogDebug("Processing batch cache update for {Count} libraries", batch.Count);

                foreach (var libId in batch)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var libService = scope.ServiceProvider.GetRequiredService<ILibraryService>();
                        await libService.UpdateRecentlyAddedCacheAsync(libId);
                        
                        // Notify Frontend to refresh
                        _notificationService.NotifyLibraryRecentUpdated(libId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to update recent cache for {LibraryId}", libId);
                    }
                }
                
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Cache Update Loop failed unexpectedly");
        }
    }

    public async Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type, bool refreshImages = true, int retryCount = 0)
    {
        // Deduplication: skip if this item is already pending processing
        if (!_pendingIds.TryAdd(mediaId, 0))
        {
            _logger.LogDebug("Skipping duplicate metadata enqueue for {MediaId}", mediaId);
            return;
        }

        var channel = GetChannelForType(type);
        await channel.Writer.WriteAsync(new MetadataQueueItem(mediaId, type, refreshImages, retryCount));
    }

    private Channel<MetadataQueueItem> GetChannelForType(LibraryType type)
    {
        // Dynamic Routing
        return type switch
        {
            LibraryType.Music => _channels["Music"],
            LibraryType.TV => _channels["TV"],
            _ => _channels["Shared"] // Movie, Book, Game, Photo
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metadata Queue Service started with Dynamic Channels.");

        var tasks = new List<Task>();

        // Start background cache update loop (with stoppingToken for graceful shutdown)
        tasks.Add(Task.Run(() => CacheUpdateLoopAsync(stoppingToken)));

        // Launch processors for each channel
        // Music: 2 Concurrent (Strict limit will bottleneck, but 2 allows overlap if provider is fast)
        tasks.Add(ProcessChannelAsync(_channels["Music"], "Music", 2, stoppingToken));

        // TV: 4 Concurrent
        tasks.Add(ProcessChannelAsync(_channels["TV"], "TV", 4, stoppingToken));

        // Shared: 10 Concurrent (Movies, Games, etc. can be retrieved very quickly from Wikidata/OMDb)
        tasks.Add(ProcessChannelAsync(_channels["Shared"], "Shared", 10, stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task ProcessChannelAsync(Channel<MetadataQueueItem> channel, string channelName, int concurrency, CancellationToken ct)
    {
         var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct
        };

        try 
        {
            await Parallel.ForEachAsync(
                channel.Reader.ReadAllAsync(ct),
                parallelOptions,
                async (item, token) => 
                {
                    try
                    {
                        await ProcessItemAsync(item, channelName, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Channel}] Error processing item {Id}", channelName, item.MediaId);
                    }
                });
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessItemAsync(MetadataQueueItem item, string channelName, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var aggregator = scope.ServiceProvider.GetRequiredService<IMetadataAggregator>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try 
        {
            var mediaItem = await context.MediaItems.FindAsync(new object[] { item.MediaId }, ct);
            if (mediaItem == null)
            {
                _logger.LogDebug("Metadata queue item {Id} not found in DB (likely deleted during rescan)", item.MediaId);
                return;
            }

            // P3-WI-003: this is the single chokepoint every refresh/enrichment path
            // funnels through (MetadataRefreshService, MetadataRetryService, every
            // scanner via _metadataQueue.EnqueueMetadataRefreshAsync). Honour the
            // admin lock here so manual matches and field edits are never overwritten.
            // ImageDownloadQueue doesn't need its own check — image enqueues happen
            // downstream of EnrichMediaItemAsync, which never runs for locked items.
            if (mediaItem.MetadataLocked)
            {
                _logger.LogDebug("Metadata queue item {Id} ({Title}) is locked; skipping enrichment", item.MediaId, mediaItem.Title);
                return;
            }

            // Snapshot MetadataHash before enrichment to detect if anything changed securely
            var metadataBefore = mediaItem.MetadataHash;

            // Enrich
            await aggregator.EnrichMediaItemAsync(mediaItem, item.Type, deferImageCaching: false, refreshImages: item.RefreshImages);

            // Save changes
            await context.SaveChangesAsync(ct);
            
            // Notify UI of item update
            _notificationService.NotifyItemUpdated(item.MediaId);

            // Retry only if enrichment produced NO change AND the item still needs enrichment.
            // This avoids retry loops for:
            //  - Album/Artist items with seeded MetadataJson (already have context)
            //  - Items where the provider legitimately found no match
            var metadataUnchanged = mediaItem.MetadataHash == metadataBefore;
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var enrichmentMode = await settingsService.GetSettingAsync("MetadataEnrichmentMode", "Relaxed");
            var strictMode = enrichmentMode == "Strict";
            var stillNeedsEnrichment = MetadataEnrichmentPolicy.NeedsEnrichment(mediaItem, strictMode);
            
            if (metadataUnchanged && stillNeedsEnrichment)
            {
                _logger.LogDebug("Metadata unchanged after enrichment for {Id} ({Title}), scheduling retry", 
                    item.MediaId, mediaItem.Title);
                var retryService = scope.ServiceProvider.GetService<IMetadataRetryService>();
                if (retryService != null)
                {
                    await retryService.EnqueueRetryAsync(item.MediaId, item.Type, item.RetryCount);
                }
            }

            // Fire event to cache channel
            _cacheUpdateChannel.Writer.TryWrite(mediaItem.LibraryId);

            ReportActivity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Channel}] Error processing metadata for {Id}", channelName, item.MediaId);
            
            // On hard exception, we also retry
            var retryService = scope.ServiceProvider.GetService<IMetadataRetryService>();
            if (retryService != null)
            {
                await retryService.EnqueueRetryAsync(item.MediaId, item.Type, item.RetryCount);
            }
        }
        finally
        {
            // Release dedup guard AFTER processing completes to prevent
            // concurrent enrichment of the same item during processing.
            _pendingIds.TryRemove(item.MediaId, out _);
        }
    }

    /// <summary>
    /// Surfaces a throttled "last active" heartbeat for the Background Tasks page. Safe
    /// under the channel's concurrency: a single winner reports per throttle window.
    /// </summary>
    private void ReportActivity()
    {
        if (_registry == null) return;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastReportTicks);
        if (now - last < ReportThrottleTicks) return;
        if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
        _registry.Report(ScheduledTaskNames.MetadataQueue, "Success");
    }

    private record MetadataQueueItem(Guid MediaId, LibraryType Type, bool RefreshImages, int RetryCount = 0);
}
