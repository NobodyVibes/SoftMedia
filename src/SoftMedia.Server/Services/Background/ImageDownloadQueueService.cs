using System.Threading;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using System.Text.Json;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Background;

public class ImageDownloadQueueService : BackgroundService, IImageDownloadQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageDownloadQueueService> _logger;
    private readonly IScheduledTaskRegistry? _registry;
    private readonly Channel<ImageDownloadRequest> _channel;
    private readonly KeyedLock _keyedLock = new(); // Striped Locking for Metadata Updates

    // Throttle for the Background Tasks heartbeat (downloads come in bursts during scans).
    private static readonly long ReportThrottleTicks = TimeSpan.FromSeconds(15).Ticks;
    private long _lastReportTicks;
    
    // Concurrency control for downloads
    // Reduced to 2 to align with rate limits (approx 2 req/sec) and avoid queue timeouts
    private const int MaxConcurrentDownloads = 2;

    public ImageDownloadQueueService(
        IServiceScopeFactory scopeFactory,
        ILogger<ImageDownloadQueueService> logger,
        IScheduledTaskRegistry? registry = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;
        _channel = Channel.CreateBounded<ImageDownloadRequest>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    // Per-library pending-download gauge, mirroring MetadataQueueService's: incremented
    // when a request is enqueued WITH a library id, decremented when it finishes. Lets the
    // scan queue hold a job's Metadata stage open until this library's artwork is cached.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _pendingByLibrary = new();

    public async Task EnqueueImageDownloadAsync(Guid mediaId, string remoteUrl, int? seasonNumber = null, int? episodeNumber = null, MediaType type = MediaType.Movie, ImageType imageType = ImageType.Poster, int? personId = null, Guid? libraryId = null)
    {
        if (libraryId.HasValue)
        {
            _pendingByLibrary.AddOrUpdate(libraryId.Value, 1, (_, count) => count + 1);
        }
        await _channel.Writer.WriteAsync(new ImageDownloadRequest(mediaId, remoteUrl, seasonNumber, episodeNumber, type, imageType, personId, libraryId));
    }

    public int GetPendingCountForLibrary(Guid libraryId)
        => _pendingByLibrary.TryGetValue(libraryId, out var count) ? count : 0;

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
                    finally
                    {
                        // Only requests counted at enqueue (LibraryId carried) decrement,
                        // so uncounted paths can never drive the gauge negative.
                        if (request.LibraryId.HasValue)
                        {
                            _pendingByLibrary.AddOrUpdate(request.LibraryId.Value, 0, (_, count) => Math.Max(0, count - 1));
                        }
                        ReportActivity();
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
                else if (request.Type == MediaType.Book)
                    localPath = await imageCacheService.CacheBookPosterAsync(request.MediaId, request.RemoteUrl);
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
                else if (request.ImageType == ImageType.SeasonPoster && request.SeasonNumber.HasValue)
                {
                    // Update specific Season Item
                    var seasonItem = await context.MediaItems
                        .FirstOrDefaultAsync(m => m.SeriesId == request.MediaId && m.SeasonNumber == request.SeasonNumber && m.Type == MediaType.Season, ct);

                    if (seasonItem != null && seasonItem.PosterUrl != localPath)
                    {
                        seasonItem.PosterUrl = localPath;
                        updated = true;
                        _logger.LogDebug("Updated Season item {Id} poster", seasonItem.Id);
                    }
                }
                else if (request.ImageType == ImageType.Still && request.SeasonNumber.HasValue && request.EpisodeNumber.HasValue)
                {
                    // Update specific Episode Item (Stills are stored in BackdropUrl for episodes)
                    var episodeItem = await context.MediaItems
                        .FirstOrDefaultAsync(m => m.SeriesId == request.MediaId && m.SeasonNumber == request.SeasonNumber && m.EpisodeNumber == request.EpisodeNumber && m.Type == MediaType.Episode, ct);

                    if (episodeItem != null && episodeItem.BackdropUrl != localPath)
                    {
                        episodeItem.BackdropUrl = localPath;
                        updated = true;
                        _logger.LogDebug("Updated Episode item {Id} still", episodeItem.Id);
                    }
                }
                else if (request.ImageType == ImageType.CastImage && request.PersonId.HasValue)
                {
                    // request.PersonId carries the provider's EXTERNAL id (CastMember.Id ->
                    // Person.ExternalId), NOT the Person primary key. FindAsync looks up by
                    // PK, so it matched nothing and the write-back was silently skipped —
                    // every cast image stayed pointed at its remote URL and was proxied on
                    // the fly even though the file had been cached. Look up by ExternalId.
                    var person = await context.Persons
                        .FirstOrDefaultAsync(p => p.ExternalId == request.PersonId.Value, ct);

                    if (person != null && person.ImagePath != localPath)
                    {
                        person.ImagePath = localPath;
                        updated = true;
                        _logger.LogDebug("Updated cast image for person ExternalId={ExternalId}", request.PersonId);
                    }
                }
                else if (!request.SeasonNumber.HasValue) // Top level item
                {
                    // R-WI-014: local art wins — a provider download that was queued BEFORE
                    // local art got applied (delayed/retried request) must not overwrite it.
                    if (request.ImageType == ImageType.Poster && !item.PosterFromLocalFile)
                    {
                        if (item.PosterUrl != localPath)
                        {
                            item.PosterUrl = localPath;
                            updated = true;
                        }
                    }
                    else if (request.ImageType == ImageType.Backdrop && !item.BackdropFromLocalFile)
                    {
                        if (item.BackdropUrl != localPath)
                        {
                            item.BackdropUrl = localPath;
                            updated = true;
                        }
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

    /// <summary>Throttled "last active" heartbeat for the Background Tasks page.</summary>
    private void ReportActivity()
    {
        if (_registry == null) return;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastReportTicks);
        if (now - last < ReportThrottleTicks) return;
        if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
        _registry.Report(ScheduledTaskNames.ImageDownloadQueue, "Success");
    }
}

public record ImageDownloadRequest(Guid MediaId, string RemoteUrl, int? SeasonNumber, int? EpisodeNumber, MediaType Type, ImageType ImageType, int? PersonId = null, Guid? LibraryId = null);
