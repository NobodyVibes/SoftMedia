using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using System.Text.Json;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Background;

public class ImageDownloadQueueService : BackgroundService, IImageDownloadQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageDownloadQueueService> _logger;
    private readonly Channel<ImageDownloadRequest> _channel;
    private readonly KeyedLock _keyedLock = new(); // Striped Locking for Metadata Updates
    
    // Concurrency control for downloads
    // Reduced to 2 to align with rate limits (approx 2 req/sec) and avoid queue timeouts
    private const int MaxConcurrentDownloads = 2;

    public ImageDownloadQueueService(
        IServiceScopeFactory scopeFactory,
        ILogger<ImageDownloadQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _channel = Channel.CreateBounded<ImageDownloadRequest>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task EnqueueImageDownloadAsync(Guid mediaId, string remoteUrl, int? seasonNumber = null, int? episodeNumber = null, MediaType type = MediaType.Movie, ImageType imageType = ImageType.Poster, int? personId = null)
    {
        await _channel.Writer.WriteAsync(new ImageDownloadRequest(mediaId, remoteUrl, seasonNumber, episodeNumber, type, imageType, personId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Image Download Queue Service started.");

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentDownloads,
            CancellationToken = stoppingToken
        };

        try
        {
            await Parallel.ForEachAsync(
                _channel.Reader.ReadAllAsync(stoppingToken),
                parallelOptions,
                async (request, ct) =>
                {
                    try
                    {
                        await ProcessDownloadAsync(request, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing image download for {MediaId}", request.MediaId);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Image Download Queue Service failed unexpectedly");
        }
    }

    private async Task ProcessDownloadAsync(ImageDownloadRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var imageCacheService = scope.ServiceProvider.GetRequiredService<IImageCacheService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<IMediaNotificationService>();

        string? localPath = null;

        try
        {
            if (request.ImageType == ImageType.Poster)
            {
                if (request.Type == MediaType.Movie)
                    localPath = await imageCacheService.CacheMoviePosterAsync(request.MediaId, request.RemoteUrl);
                else if (request.Type == MediaType.Series)
                    localPath = await imageCacheService.CacheSeriesPosterAsync(request.MediaId, request.RemoteUrl);
                else if (request.Type == MediaType.Game)
                    localPath = await imageCacheService.CacheGamePosterAsync(request.MediaId, request.RemoteUrl);
            }
            else if (request.ImageType == ImageType.AlbumCover)
            {
                 localPath = await imageCacheService.CacheAlbumCoverAsync(request.MediaId, request.RemoteUrl);
            }
            else if (request.ImageType == ImageType.SeasonPoster && request.SeasonNumber.HasValue)
            {
                localPath = await imageCacheService.CacheSeasonPosterAsync(request.MediaId, request.SeasonNumber.Value, request.RemoteUrl);
            }
            else if (request.ImageType == ImageType.Still && request.SeasonNumber.HasValue && request.EpisodeNumber.HasValue)
            {
                localPath = await imageCacheService.CacheEpisodeStillAsync(request.MediaId, request.SeasonNumber.Value, request.EpisodeNumber.Value, request.RemoteUrl);
            }
            else if (request.ImageType == ImageType.CastImage && request.PersonId.HasValue)
            {
                localPath = await imageCacheService.CacheCastImageAsync(request.PersonId.Value, request.RemoteUrl);
            }
        }
        catch (Exception ex)
        {
             _logger.LogWarning(ex, "Failed to download image from {Url}", request.RemoteUrl);
             return;
        }

        if (string.IsNullOrEmpty(localPath) || localPath == request.RemoteUrl)
        {
            return;
        }

        // CRITICAL: Lock based on MediaId to prevent race conditions on MetadataJson updates.
        // This ensures that if multiple images (Season 1, Season 2, etc.) finish at the same time,
        // they update the database sequentially, preventing "Lost Updates".
        using (await _keyedLock.LockAsync(request.MediaId))
        {
            try
            {
                // Re-fetch item inside the lock to get the latest version
                // Note: We use a new scope/context or verify correct tracking. 
                // Since this method uses a scoped context 'context', efficient re-fetching is key.
                // However, since we are inside a Parallel.ForEach, 'context' is unique to this Task execution.
                // But we need to ensure we are reading the COMMITTED state from the DB.
                // EF Core's default tracking might return stale data if we had fetched it earlier (we haven't).
                // So finding it now is correct.
                
                var item = await context.MediaItems.FindAsync(new object[] { request.MediaId }, ct);
                if (item == null) return;

                // Reload to ensure we have the absolute latest data from DB (in case another thread updated it)
                await context.Entry(item).ReloadAsync(ct);

                bool updated = false;

                if (request.ImageType == ImageType.AlbumCover)
                {
                    if (item.CoverArtPath != localPath)
                    {
                        item.CoverArtPath = localPath;
                        updated = true;
                    }
                }
                else 
                {
                    if (string.IsNullOrEmpty(item.MetadataJson))
                    {
                        item.MetadataJson = "{}";
                    }

                    var meta = MetadataJsonHelper.Parse(item.MetadataJson);
                    
                    string key = request.ImageType.ToString().ToLowerInvariant();
                    if (request.ImageType == ImageType.Backdrop) key = "backdrop";

                    // Handle Season/Episode updates in JSON
                    if (request.ImageType == ImageType.SeasonPoster && request.SeasonNumber.HasValue)
                    {
                        // Update Series Metadata
                        if (meta.ContainsKey("seasons") && meta["seasons"] is JsonElement seasonsEl)
                        {
                             var seasons = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(seasonsEl.GetRawText());
                             if (seasons != null)
                             {
                                 var season = seasons.FirstOrDefault(s => s.ContainsKey("number") && s["number"].ToString() == request.SeasonNumber.ToString());
                                 if (season != null)
                                 {
                                     season["poster"] = localPath;
                                     meta["seasons"] = seasons;
                                     updated = true;
                                 }
                             }
                        }

                        // Update specific Season Item
                        var seasonItem = await context.MediaItems
                            .FirstOrDefaultAsync(m => m.SeriesId == request.MediaId && m.SeasonNumber == request.SeasonNumber && m.Type == MediaType.Season, ct);

                        if (seasonItem != null)
                        {
                            var seasonMeta = string.IsNullOrEmpty(seasonItem.MetadataJson) 
                                ? new Dictionary<string, object>() 
                                : MetadataJsonHelper.Parse(seasonItem.MetadataJson);
                            
                            seasonMeta["poster"] = localPath;
                            seasonItem.MetadataJson = JsonSerializer.Serialize(seasonMeta);
                            updated = true;
                            _logger.LogDebug("Updated Season item {Id} poster", seasonItem.Id);
                        }
                    }
                    else if (request.ImageType == ImageType.Still && request.SeasonNumber.HasValue && request.EpisodeNumber.HasValue)
                    {
                         // Update Series Metadata (Episode list)
                         if (meta.ContainsKey("episodes") && meta["episodes"] is JsonElement epsEl)
                         {
                             var episodes = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(epsEl.GetRawText());
                             if (episodes != null)
                             {
                                 var ep = episodes.FirstOrDefault(e => 
                                     e.ContainsKey("season") && e["season"].ToString() == request.SeasonNumber.ToString() &&
                                     e.ContainsKey("episode") && e["episode"].ToString() == request.EpisodeNumber.ToString());
                                 
                                 if (ep != null)
                                 {
                                     ep["still"] = localPath;
                                     meta["episodes"] = episodes;
                                     updated = true;
                                 }
                             }
                         }

                         // Update specific Episode Item
                        var episodeItem = await context.MediaItems
                            .FirstOrDefaultAsync(m => m.SeriesId == request.MediaId && m.SeasonNumber == request.SeasonNumber && m.EpisodeNumber == request.EpisodeNumber && m.Type == MediaType.Episode, ct);

                        if (episodeItem != null)
                        {
                            var epMeta = string.IsNullOrEmpty(episodeItem.MetadataJson) 
                                ? new Dictionary<string, object>() 
                                : MetadataJsonHelper.Parse(episodeItem.MetadataJson);
                            
                            epMeta["still"] = localPath;
                            episodeItem.MetadataJson = JsonSerializer.Serialize(epMeta);
                            updated = true;
                            _logger.LogDebug("Updated Episode item {Id} still", episodeItem.Id);
                        }
                    }
                    else if (request.ImageType == ImageType.CastImage && request.PersonId.HasValue)
                    {
                        // Update the specific cast member's image in the cast array
                        if (meta.ContainsKey("cast") && meta["cast"] is JsonElement castEl)
                        {
                            var castList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(castEl.GetRawText());
                            if (castList != null)
                            {
                                var member = castList.FirstOrDefault(c =>
                                    c.ContainsKey("id") && c["id"].ToString() == request.PersonId.ToString());

                                if (member != null)
                                {
                                    member["image"] = localPath;
                                    meta["cast"] = castList;
                                    updated = true;
                                    _logger.LogDebug("Updated cast image for person {PersonId}", request.PersonId);
                                }
                            }
                        }
                    }
                    else if (!request.SeasonNumber.HasValue) // Top level item
                    {
                        if (!meta.ContainsKey(key) || meta[key].ToString() != localPath)
                        {
                            meta[key] = localPath;
                            updated = true;
                        }
                    }

                    if (updated)
                    {
                        item.MetadataJson = JsonSerializer.Serialize(meta);
                    }
                }

                if (updated)
                {
                    await context.SaveChangesAsync(ct);
                    
                    // Invalidate caches so home page and hero show local image URLs
                    await InvalidateCachesAsync(context, item.LibraryId, ct);
                    
                    notificationService.NotifyItemUpdated(item.Id);
                    _logger.LogDebug("Updated MediaItem {Id} image ({Type}): {Path}", item.Id, request.ImageType, localPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update database for media {Id}", request.MediaId);
            }
        }
    }

    /// <summary>
    /// Invalidate LibraryRecentCache and HeroCache after image downloads so
    /// the home page and hero sections use local (cached) image URLs.
    /// Absorbed from BackgroundImageCacheService.
    /// </summary>
    private async Task InvalidateCachesAsync(AppDbContext context, Guid? libraryId, CancellationToken ct)
    {
        try
        {
            if (libraryId.HasValue)
            {
                var cacheEntry = await context.LibraryRecentCaches
                    .FirstOrDefaultAsync(c => c.LibraryId == libraryId.Value, ct);
                if (cacheEntry != null)
                {
                    context.LibraryRecentCaches.Remove(cacheEntry);
                    await context.SaveChangesAsync(ct);
                }
            }

            var heroCache = await context.HeroCaches.FirstOrDefaultAsync(c => c.Id == 1, ct);
            if (heroCache != null)
            {
                context.HeroCaches.Remove(heroCache);
                await context.SaveChangesAsync(ct);
                _logger.LogDebug("Invalidated hero cache after image download");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate caches after image download");
        }
    }
}

public record ImageDownloadRequest(Guid MediaId, string RemoteUrl, int? SeasonNumber, int? EpisodeNumber, MediaType Type, ImageType ImageType, int? PersonId = null);
