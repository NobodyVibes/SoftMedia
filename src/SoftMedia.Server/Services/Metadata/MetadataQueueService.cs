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
    
    // Rate Limiter
    // Key: Provider Name (e.g. "MusicBrainz", "TVMaze")
    private readonly PartitionedRateLimiter<string> _limiter;

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

        // Initialize Rate Limiter
        _limiter = PartitionedRateLimiter.Create<string, string>(providerName =>
        {
            // Default rules
            int permitLimit = 5;
            TimeSpan window = TimeSpan.FromSeconds(1);

            switch (providerName.ToLowerInvariant())
            {
                case "musicbrainz":
                    // strict 1 req/sec
                    permitLimit = 1; 
                    window = TimeSpan.FromSeconds(1.1); 
                    break;
                case "tvmaze":
                    // ~2 req/sec
                    permitLimit = 2; 
                    window = TimeSpan.FromSeconds(1);
                    break;
                case "wikidata":
                case "omdb": // OMDb is fast but paid; strict limit might be needed if user has low tier key, but 10 is safe default.
                case "igdb": 
                case "open library":
                    permitLimit = 10; 
                    window = TimeSpan.FromSeconds(1);
                    break;
                case "local":
                case "embedded":
                case "exif":
                case "none":
                    permitLimit = 100; // Unlimited effectively
                    window = TimeSpan.FromSeconds(1);
                    break;
            }

            return RateLimitPartition.GetFixedWindowLimiter(providerName,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 1000
                });
        });
        
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

    public async Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type, bool refreshImages = true)
    {
        var channel = GetChannelForType(type);
        await channel.Writer.WriteAsync(new MetadataQueueItem(mediaId, type, refreshImages));
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
                        // 1. Determine Provider (for Rate Limiting)
                        string providerName = await GetProviderNameAsync(item.Type);

                        // 2. Acquire Rate Limit Lease
                        // We use the Provider Name as the partition key.
                        // This ensures that even if we are processing 10 movies in parallel,
                        // if they all use the same provider, they will respect the rate limit.
                        using var lease = await _limiter.AcquireAsync(providerName, permitCount: 1, cancellationToken: token);

                        if (lease.IsAcquired)
                        {
                            await ProcessItemAsync(item, token);
                        }
                        else
                        {
                            _logger.LogWarning("Rate Limit Lease failed for {Provider}. Re-queuing {Id}", providerName, item.MediaId);
                            // Simple retry by writing back to channel? 
                            // Risk of infinite loop if queue is full. 
                            // Better to just log. Ideally RateLimiter waits until available with AcquireAsync.
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing item {Id} in channel {Channel}", item.MediaId, channelName);
                    }
                });
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string> GetProviderNameAsync(LibraryType type)
    {
        // Use a scope to resolve SettingsService
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        return type switch
        {
            LibraryType.Movie => await settings.GetSettingAsync("MovieProvider", "Wikidata"),
            LibraryType.TV => await settings.GetSettingAsync("TVProvider", "TVMaze"),
            LibraryType.Music => await settings.GetSettingAsync("MusicProvider", "MusicBrainz"),
            LibraryType.Book => await settings.GetSettingAsync("BookProvider", "Open Library"),
            LibraryType.Game => await settings.GetSettingAsync("GameProvider", "Wikidata"),
            LibraryType.Photo => await settings.GetSettingAsync("PhotoProvider", "Exif"),
            _ => "Default"
        };
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

            // Mark library as dirty for cache update
            _dirtyLibraries.TryAdd(mediaItem.LibraryId, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing metadata for {Id}", item.MediaId);
        }
    }

    private record MetadataQueueItem(Guid MediaId, LibraryType Type, bool RefreshImages);
}
