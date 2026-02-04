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
    private readonly Channel<MetadataQueueItem> _channel;
    private readonly PartitionedRateLimiter<MetadataQueueItem> _limiter;

    // Track dirty state of libraries for cache update
    private readonly ConcurrentDictionary<Guid, bool> _dirtyLibraries = new();
    private readonly Task _cacheUpdateLoop;

    public MetadataQueueService(
        IServiceScopeFactory scopeFactory,
        IMediaNotificationService notificationService,
        ILogger<MetadataQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _notificationService = notificationService;
        _logger = logger;
        _channel = Channel.CreateBounded<MetadataQueueItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _limiter = PartitionedRateLimiter.Create<MetadataQueueItem, string>(resource =>
        {
            // Define limits based on LibraryType (approximating provider usage)
            return resource.Type switch
            {
                // MusicBrainz: VERY strict (1 req/sec)
                LibraryType.Music => RateLimitPartition.GetFixedWindowLimiter("Music",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromSeconds(1.1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 100
                    }),

                // TVMaze: 20 req / 10s => ~2 req/sec
                LibraryType.TV => RateLimitPartition.GetFixedWindowLimiter("TV",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 2,
                        Window = TimeSpan.FromSeconds(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 100
                    }),

                // Default (Movies/Games - Wikidata/OMDb): Moderate
                _ => RateLimitPartition.GetFixedWindowLimiter("Default",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromSeconds(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 100
                    })
            };
        });
        
        // Start background cache update loop
        _cacheUpdateLoop = Task.Run(CacheUpdateLoopAsync);
    }
    
    private async Task CacheUpdateLoopAsync()
    {
        _logger.LogInformation("Recently Added Cache Update Loop started.");
        try
        {
            while (true)
            {
                // Check every 2 seconds
                await Task.Delay(2000);
                
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

    public async Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type)
    {
        await _channel.Writer.WriteAsync(new MetadataQueueItem(mediaId, type));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metadata Queue Service started.");

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try 
            {
                // Wait for rate limiter
                using var lease = await _limiter.AcquireAsync(item, permitCount: 1, cancellationToken: stoppingToken);
                
                if (lease.IsAcquired)
                {
                    await ProcessItemAsync(item, stoppingToken);
                }
                else
                {
                    _logger.LogWarning("Rate Limit Exceeded for {Type}. Re-queuing {Id}", item.Type, item.MediaId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing metadata item {Id}", item.MediaId);
            }
        }
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
                _logger.LogWarning("Metadata queue item {Id} not found in DB", item.MediaId);
                return;
            }

            // Enrich
            await aggregator.EnrichMediaItemAsync(mediaItem, item.Type, deferImageCaching: false, refreshImages: true);

            // Save changes
            await context.SaveChangesAsync(ct);
            
            // Notify UI of item update
            _notificationService.NotifyItemUpdated(item.MediaId);

            // Mark library as dirty for cache update
            _dirtyLibraries.TryAdd(mediaItem.LibraryId, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing metadata for {Id}", item.MediaId);
        }
    }

    private record MetadataQueueItem(Guid MediaId, LibraryType Type);
}
