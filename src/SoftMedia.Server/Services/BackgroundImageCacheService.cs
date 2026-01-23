using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

/// <summary>
/// Background service that processes a queue of media items to cache their images.
/// Runs asynchronously to avoid blocking library scans.
/// </summary>
public class BackgroundImageCacheService : BackgroundService, IBackgroundImageCacheService
{
    private readonly Channel<Guid> _queue;
    private readonly HashSet<Guid> _queuedIds = new();
    private readonly object _lock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundImageCacheService> _logger;
    private readonly IMediaNotificationService _notificationService;
    
    private const int MaxQueueSize = 1000;
    private static readonly TimeSpan DelayBetweenItems = TimeSpan.FromMilliseconds(100);

    public BackgroundImageCacheService(
        IServiceScopeFactory scopeFactory,
        ILogger<BackgroundImageCacheService> logger,
        IMediaNotificationService notificationService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notificationService = notificationService;
        _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void QueueImageCaching(Guid mediaItemId)
    {
        lock (_lock)
        {
            if (_queuedIds.Contains(mediaItemId))
            {
                _logger.LogDebug("Skipping duplicate queue entry for {Id}", mediaItemId);
                return;
            }
            
            if (_queue.Writer.TryWrite(mediaItemId))
            {
                _queuedIds.Add(mediaItemId);
                _logger.LogDebug("Queued image caching for {Id}", mediaItemId);
            }
            else
            {
                _logger.LogWarning("Failed to queue image caching for {Id} - queue may be full", mediaItemId);
            }
        }
    }

    public int GetQueueDepth() => _queue.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundImageCacheService started");
        
        try
        {
            await foreach (var itemId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessItemAsync(itemId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("BackgroundImageCacheService stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error caching images for {Id}", itemId);
                }
                finally
                {
                    lock (_lock) { _queuedIds.Remove(itemId); }
                }
                
                // Rate limiting to avoid hammering image servers
                if (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(DelayBetweenItems, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        
        _logger.LogInformation("BackgroundImageCacheService stopped");
    }

    private async Task ProcessItemAsync(Guid itemId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imageCache = scope.ServiceProvider.GetRequiredService<ImageCacheService>();
        
        // Retry loop to handle race condition where scanner hasn't committed new item yet
        MediaItem? item = null;
        for (int i = 0; i < 5; i++)
        {
            item = await context.MediaItems.FindAsync(new object[] { itemId }, ct);
            if (item != null) break;
            
            // Wait for DB commit
            await Task.Delay(500, ct); 
        }

        if (item == null)
        {
            _logger.LogWarning("Media item {Id} not found after retries, skipping image cache", itemId);
            return;
        }
        
        if (string.IsNullOrEmpty(item.MetadataJson))
        {
            _logger.LogDebug("Media item {Id} has no metadata, skipping image cache", itemId);
            return;
        }
        
        Dictionary<string, object>? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse metadata for {Id}", itemId);
            return;
        }
        
        if (metadata == null) return;
        
        bool modified = false;
        
        // Cache poster for all media types
        _logger.LogInformation("Processing background cache for {Title}. Metadata contains keys: {Keys}", item.Title, string.Join(", ", metadata.Keys));
        modified |= await CachePosterAsync(item, metadata, imageCache);
        
        // For Series: also cache season posters and episode stills
        if (item.Type == MediaType.Series)
        {
            // Fetch existing Seasons and Episodes WITH tracking to update them
            var existingSeasons = await context.MediaItems
                .Where(m => m.SeriesId == item.Id && m.Type == MediaType.Season && m.SeasonNumber.HasValue)
                .ToListAsync(ct);

            var existingEpisodes = await context.MediaItems
                .Where(m => m.SeriesId == item.Id && m.Type == MediaType.Episode && m.SeasonNumber.HasValue && m.EpisodeNumber.HasValue)
                .ToListAsync(ct);

            modified |= await CacheSeasonPostersAsync(item.Id, metadata, imageCache, existingSeasons);
            modified |= await CacheEpisodeStillsAsync(item.Id, metadata, imageCache, existingEpisodes);
        }
        
        if (modified)
        {
            item.MetadataJson = JsonSerializer.Serialize(metadata);
            // Save changes to Series and any modified Season/Episode items
            await context.SaveChangesAsync(ct);
            _logger.LogInformation("Background cached images for {Type}: {Title}", item.Type, item.Title);
            
            // Push real-time update to any clients viewing this item
            _notificationService.NotifyItemUpdated(itemId);
        }
    }

    private async Task<bool> CachePosterAsync(MediaItem item, Dictionary<string, object> metadata, ImageCacheService imageCache)
    {
        if (!metadata.TryGetValue("poster", out var posterObj))
        {
            _logger.LogDebug("No poster key in metadata for {Title}", item.Title);
            return false;
        }
        
        var posterUrl = posterObj.ToString();
        _logger.LogInformation("Background: Found poster URL for {Title} ({Type}): {Url}", item.Title, item.Type, posterUrl);
        
        if (string.IsNullOrEmpty(posterUrl) || !posterUrl.StartsWith("http"))
        {
            _logger.LogDebug("Poster URL invalid or not HTTP for {Title}: {Url}", item.Title, posterUrl);
            return false;
        }
        
        // Already cached locally
        if (posterUrl.StartsWith("/cache/"))
        {
            _logger.LogDebug("Poster already cached for {Title}", item.Title);
            return false;
        }
        
        try
        {
            _logger.LogInformation("Background: Caching poster for {Title} (Type: {Type})", item.Title, item.Type);
            string cachedUrl = item.Type switch
            {
                MediaType.Series => await imageCache.CacheSeriesPosterAsync(item.Id, posterUrl),
                MediaType.Movie => await imageCache.CacheMoviePosterAsync(item.Id, posterUrl),
                MediaType.Audio or MediaType.Album => await imageCache.CacheAlbumCoverAsync(item.Id, posterUrl),
                _ => posterUrl
            };
            
            if (cachedUrl != posterUrl)
            {
                metadata["poster"] = cachedUrl;
                _logger.LogInformation("Background: Cached poster for {Title}: {Url}", item.Title, cachedUrl);
                return true;
            }
            else
            {
                _logger.LogWarning("Background: Caching returned same URL for {Title}, caching may have failed", item.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache poster for {Title}", item.Title);
        }
        
        return false;
    }

    private async Task<bool> CacheSeasonPostersAsync(Guid seriesId, Dictionary<string, object> metadata, ImageCacheService imageCache, List<MediaItem> existingSeasons)
    {
        if (!metadata.TryGetValue("seasons", out var seasonsObj) || seasonsObj is not JsonElement seasonsArray)
            return false;
        
        var seasonsList = new List<Dictionary<string, object?>>();
        bool modified = false;
        
        foreach (var season in seasonsArray.EnumerateArray())
        {
            var seasonDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(season.GetRawText()) ?? new();
            
            try
            {
                if (seasonDict.TryGetValue("number", out var numObj) && numObj != null &&
                    seasonDict.TryGetValue("poster", out var seasonPosterObj) && seasonPosterObj != null)
                {
                    var seasonNum = Convert.ToInt32(numObj.ToString());
                    var seasonPosterUrl = seasonPosterObj.ToString();
                    
                    // Find matching season entities (plural to handle duplicates)
                    var matchingSeasons = existingSeasons
                        .Where(s => s.SeasonNumber == seasonNum)
                        .ToList();

                    if (matchingSeasons.Any() && 
                        !string.IsNullOrEmpty(seasonPosterUrl) && 
                        seasonPosterUrl.StartsWith("http") && 
                        !seasonPosterUrl.StartsWith("/cache/"))
                    {
                        var cachedUrl = await imageCache.CacheSeasonPosterAsync(seriesId, seasonNum, seasonPosterUrl);
                        if (cachedUrl != seasonPosterUrl)
                        {
                            seasonDict["poster"] = cachedUrl;
                            modified = true;
                            _logger.LogDebug("Cached season {Season} poster", seasonNum);
                            
                            // Update ALL matching Season Entity Metadata
                            foreach (var seasonEntity in matchingSeasons)
                            {
                                UpdateSeasonEntityMetadata(seasonEntity, cachedUrl);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache season poster");
            }
            
            seasonsList.Add(seasonDict);
        }
        
        if (modified)
        {
            metadata["seasons"] = seasonsList;
        }
        
        return modified;
    }

    private void UpdateSeasonEntityMetadata(MediaItem season, string cachedUrl)
    {
        try
        {
            var meta = string.IsNullOrEmpty(season.MetadataJson) 
                ? new Dictionary<string, object>() 
                : JsonSerializer.Deserialize<Dictionary<string, object>>(season.MetadataJson) ?? new Dictionary<string, object>();
            
            meta["poster"] = cachedUrl;
            season.MetadataJson = JsonSerializer.Serialize(meta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update season entity metadata for {Id}", season.Id);
        }
    }

    private async Task<bool> CacheEpisodeStillsAsync(Guid seriesId, Dictionary<string, object> metadata, ImageCacheService imageCache, List<MediaItem> existingEpisodes)
    {
        if (!metadata.TryGetValue("episodes", out var episodesObj) || episodesObj is not JsonElement episodesArray)
            return false;
        
        var episodesList = new List<Dictionary<string, object?>>();
        bool modified = false;
        
        foreach (var episode in episodesArray.EnumerateArray())
        {
            var epDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(episode.GetRawText()) ?? new();
            
            try
            {
                if (epDict.TryGetValue("season", out var seasonObj) &&
                    epDict.TryGetValue("episode", out var epNumObj) &&
                    epDict.TryGetValue("still", out var stillObj) && stillObj != null)
                {
                    var epSeason = seasonObj != null ? Convert.ToInt32(seasonObj.ToString()) : 0;
                    var epNum = epNumObj != null ? Convert.ToInt32(epNumObj.ToString()) : 0;
                    var stillUrl = stillObj.ToString();
                    
                    // Find matching episode entities (plural to handle duplicates)
                    var matchingEpisodes = existingEpisodes
                        .Where(e => e.SeasonNumber == epSeason && e.EpisodeNumber == epNum)
                        .ToList();

                    if (matchingEpisodes.Any() && 
                        epNum > 0 && 
                        !string.IsNullOrEmpty(stillUrl) && 
                        stillUrl.StartsWith("http") && 
                        !stillUrl.StartsWith("/cache/"))
                    {
                        var cachedUrl = await imageCache.CacheEpisodeStillAsync(seriesId, epSeason, epNum, stillUrl);
                        if (cachedUrl != stillUrl)
                        {
                            epDict["still"] = cachedUrl;
                            modified = true;
                            _logger.LogDebug("Cached S{Season}E{Episode} still for {Count} items", epSeason, epNum, matchingEpisodes.Count);

                            // Update ALL matching Episode Entity Metadata
                            foreach (var epEntity in matchingEpisodes)
                            {
                                UpdateEpisodeEntityMetadata(epEntity, cachedUrl);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache episode still");
            }
            
            episodesList.Add(epDict);
        }
        
        if (modified)
        {
            metadata["episodes"] = episodesList;
        }
        
        return modified;
    }

    private void UpdateEpisodeEntityMetadata(MediaItem episode, string cachedUrl)
    {
        try
        {
            var meta = string.IsNullOrEmpty(episode.MetadataJson) 
                ? new Dictionary<string, object>() 
                : JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson) ?? new Dictionary<string, object>();
            
            meta["still"] = cachedUrl;
            episode.MetadataJson = JsonSerializer.Serialize(meta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update episode entity metadata for {Id}", episode.Id);
        }
    }
}
