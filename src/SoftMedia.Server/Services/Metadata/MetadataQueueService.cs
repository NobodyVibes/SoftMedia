using System.Threading.Channels;
using System.Threading.RateLimiting;
using SoftMedia.Server.Services.Abstractions;
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

    // Dynamic Channel Router
    // Key: Channel Name (Music, TV, Shared)
    private readonly Dictionary<string, Channel<MetadataQueueItem>> _channels;
    
    // Track dirty state of libraries for cache update
    private readonly ConcurrentDictionary<Guid, bool> _dirtyLibraries = new();

    public MetadataQueueService(
        IServiceScopeFactory scopeFactory,
        IMediaNotificationService notificationService,
        ILogger<MetadataQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _notificationService = notificationService;
        _logger = logger;

        // Initialize Channels with appropriate capacities
        // Music: High capacity because scans are large, but processing is slow.
        // TV/Shared: Moderate capacity.
        _channels = new Dictionary<string, Channel<MetadataQueueItem>>
        {
            { "Music", Channel.CreateBounded<MetadataQueueItem>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.Wait }) },
            { "TV", Channel.CreateBounded<MetadataQueueItem>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.Wait }) },
            { "Shared", Channel.CreateBounded<MetadataQueueItem>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.Wait }) }
        };
        
    }
    
    private async Task CacheUpdateLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Recently Added Cache Update Loop started.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Check every 2 seconds
                await Task.Delay(2000, ct);
                
                var dirtyLibs = _dirtyLibraries.Keys.ToList();
                foreach (var libId in dirtyLibs)
                {
                    if (_dirtyLibraries.TryRemove(libId, out _))
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var libService = scope.ServiceProvider.GetRequiredService<ILibraryService>();
                            await libService.UpdateRecentlyAddedCacheAsync(libId);
                            
                            // Notify Frontend to refresh
                            _notificationService.NotifyLibraryRecentUpdated(libId);
                            _logger.LogDebug("Updated cache and notified for Library {LibraryId}", libId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update recent cache for {LibraryId}", libId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Cache Update Loop failed unexpectedly");
        }
    }

    public async Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type, bool refreshImages = true, int retryCount = 0)
    {
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
                        await ProcessItemAsync(item, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing item {Id} in channel {Channel}", item.MediaId, channelName);
                    }
                });
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessItemAsync(MetadataQueueItem item, CancellationToken ct)
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

            // Enrich
            await aggregator.EnrichMediaItemAsync(mediaItem, item.Type, deferImageCaching: false, refreshImages: item.RefreshImages);

            // Save changes
            await context.SaveChangesAsync(ct);
            
            // Notify UI of item update
            _notificationService.NotifyItemUpdated(item.MediaId);

            // If still no metadata, enqueue for retry
            if (string.IsNullOrEmpty(mediaItem.MetadataJson) || mediaItem.MetadataJson == "{}")
            {
                var retryService = scope.ServiceProvider.GetService<IMetadataRetryService>();
                if (retryService != null)
                {
                    retryService.EnqueueRetry(item.MediaId, item.Type, item.RetryCount);
                }
            }

            // Mark library as dirty for cache update
            _dirtyLibraries.TryAdd(mediaItem.LibraryId, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing metadata for {Id}", item.MediaId);
            
            // On hard exception, we also retry
            var retryService = scope.ServiceProvider.GetService<IMetadataRetryService>();
            if (retryService != null)
            {
                retryService.EnqueueRetry(item.MediaId, item.Type, item.RetryCount);
            }
        }
    }

    private record MetadataQueueItem(Guid MediaId, LibraryType Type, bool RefreshImages, int RetryCount = 0);
}
