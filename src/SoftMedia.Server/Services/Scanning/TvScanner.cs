using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using System.Text.Json;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for TV show libraries. Handles Series/Season/Episode hierarchy.
/// </summary>
public class TvScanner : BaseMediaScanner
{
    private readonly IBackgroundImageCacheService _backgroundImageCache;
    private readonly IMediaAnalysisService _mediaAnalysisService;

    // Supported video extensions
    private static readonly string[] VideoExtensions =
    {
        "mkv", "mp4", "avi", "m4v", "wmv", "mov", "webm", "ts", "m2ts"
    };

    public override LibraryType SupportedType => LibraryType.TV;
    public override string[] SupportedExtensions => VideoExtensions;
    public override string DisplayName => "TV Scanner";

    // Session caches to avoid repeated DB queries
    private Dictionary<string, MediaItem> _seriesCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(Guid SeriesId, int SeasonNum), MediaItem> _seasonCache = new();
    
    // Track new series IDs for deferred image caching (to avoid race condition)
    private HashSet<Guid> _newSeriesIds = new();



    public TvScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<TvScanner> logger,
        IMediaNotificationService notificationService,
        IBackgroundImageCacheService backgroundImageCache,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _backgroundImageCache = backgroundImageCache;
        _mediaAnalysisService = mediaAnalysisService;
    }

    /// <summary>
    /// Override to clear session caches at start of scan.
    /// </summary>
    public override async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Clear session caches
        _seriesCache.Clear();
        _seasonCache.Clear();
        _newSeriesIds.Clear();

        await base.ScanLibraryAsync(library, progress, cancellationToken);
        
        // Queue image caching for new series AFTER all episodes/seasons are created.
        // Actually, since we queue metadata enrichment, we don't need to manually queue images here anymore?
        // MetadataQueueService handles enrichment AND image caching trigger if needed.
        // But if TvScanner relied on Series JSON containing season/episode images...
        // Let's keep existing logic safe, but _newSeriesIds populate might change.
        // Since we enqueue Series to MetadataQueue, that usually handles simple image caching.
        // If "Deferred Image Caching" pattern is used by QueueService... 
        // MetadataQueueService calls Aggregator with deferImageCaching:false.
        // So images are cached immediately by the background worker.
        // So we don't need this manual queue.
    }


    /// <summary>
    /// Process a single video file as a TV episode.
    /// </summary>
    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        try
        {
            // ... (keep existing parsing logic omitted for brevity, focusing on signature/return) ...
            // Wait, replace_file_content replaces the BLOCK. I must include the parsing logic or target closely.
            // I'll target the whole method to be safe relative to the view.
            
            // Parse episode info from filename
            var parsed = FileNameParser.ParseTvEpisode(filePath);
            var showName = parsed.ShowName;
            var seasonNum = parsed.Season;
            var episodeNum = parsed.Episode;
            var episodeTitle = parsed.EpisodeTitle;

            // If parsing failed, try to extract from directory structure
            if (string.IsNullOrEmpty(showName) || (seasonNum == 0 && episodeNum == 0))
            {
                var dirInfo = ParseTvInfoFromDirectory(filePath);
                if (string.IsNullOrEmpty(showName))
                    showName = dirInfo.ShowName;
                if (seasonNum == 0)
                    seasonNum = dirInfo.Season;
            }

            if (string.IsNullOrEmpty(showName))
                showName = "Unknown Show";

            // Clean the show name
            var cleanedShowName = FileNameParser.CleanShowName(showName);
            if (!string.IsNullOrEmpty(cleanedShowName))
                showName = cleanedShowName;

            var showYear = FileNameParser.ExtractYear(showName);

            // Ensure series exists (Thread Safe)
            var series = await EnsureSeriesAsync(context, showName, showYear, library, filePath, cancellationToken);

            // Ensure season exists (Thread Safe)
            var season = await EnsureSeasonAsync(context, series, seasonNum, library, cancellationToken);

            // Create or update episode
            var isNew = existing == null;
            var episode = existing ?? new MediaItem { LibraryId = library.Id };

            // Determine episode title
            var finalTitle = !string.IsNullOrEmpty(episodeTitle) 
                ? episodeTitle 
                : $"Episode {episodeNum}";

            if (!isNew)
            {
               // If existing, try to keep existing title if not generic
               if (episode.Title != $"Episode {episode.EpisodeNumber}" && string.IsNullOrEmpty(episodeTitle))
                   finalTitle = episode.Title;
            }

            // Try to get authoritative title from TVMaze metadata (cached on Series)
            var tvMazeTitle = GetEpisodeTitleFromMetadata(series, seasonNum, episodeNum);
            if (!string.IsNullOrEmpty(tvMazeTitle))
                finalTitle = tvMazeTitle;

            episode.Title = finalTitle;
            episode.SortTitle = $"S{seasonNum:D2}E{episodeNum:D3}";
            episode.Path = filePath;
            episode.Type = MediaType.Episode;
            episode.SeriesId = series.Id;
            episode.SeasonId = season.Id;
            episode.SeasonNumber = seasonNum;
            episode.EpisodeNumber = episodeNum;
            episode.Size = new FileInfo(filePath).Length;
            episode.DateModified = File.GetLastWriteTimeUtc(filePath);

            // Delegate technical analysis to MediaAnalysisService (Smart Probe)
            var refreshMode = isNew ? MetadataRefreshMode.Full : MetadataRefreshMode.Missing;
            await _mediaAnalysisService.AnalyzeAsync(episode, filePath, refreshMode, cancellationToken);

            // Populate episode metadata from series (still image, summary, airdate)
            PopulateEpisodeMetadata(episode, series, seasonNum, episodeNum);

            if (isNew)
            {
                context.MediaItems.Add(episode);
                
                // Enqueue enrichment if we didn't get good metadata from series cache
                var shouldEnqueue = string.IsNullOrEmpty(tvMazeTitle);

                _logger.LogDebug("[TvScanner] Added episode: {Show} S{Season}E{Episode} - {Title}",
                    showName, seasonNum, episodeNum, finalTitle);
                return new ScanOperationResult(ScanResult.New, episode.Id, EnqueueMetadata: shouldEnqueue);
            }
            else
            {
                _logger.LogDebug("[TvScanner] Updated episode: {Show} S{Season}E{Episode}",
                    showName, seasonNum, episodeNum);
                return new ScanOperationResult(ScanResult.Updated, episode.Id, EnqueueMetadata: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TvScanner] Error processing file: {FilePath}", filePath);
            return new ScanOperationResult(ScanResult.Skipped);
        }
    }

    /// <summary>
    /// Get or create a series entity.
    /// </summary>
    private async Task<MediaItem> EnsureSeriesAsync(
        AppDbContext context,
        string showName,
        int? year,
        Library library,
        string episodePath,
        CancellationToken cancellationToken)
    {
        // Check session cache first (Thread safe? No, Dict is not TS, but we are inside a thread for a directory. 
        // BaseMediaScanner loop is parallel directories.
        // _seriesCache is global for the scanner instance? 
        // TvScanner is instantiated ONCE (Singleton/Scoped)?
        // BaseMediaScanner is usually Scoped/Transient.
        // If Scoped: created per request. 
        // Implementation check: IMediaScanner registration.
        // But Parallel loop is inside ONE instance of Scanner.
        // So _seriesCache access IS NOT THREAD SAFE.
        // FIX: Remove usage of _seriesCache or make it ConcurrentDictionary.
        // Or strictly rely on DB + Lock.
        // Given concurrency, local cache per thread (directory) is safest, but misses cross-directory cache hits.
        // Global ConcurrentDictionary is better.
        
        // For now, let's use the DB + Lock pattern primarily.
        
        // Critical Section: Create Series
        using (await LockParentAsync(showName, cancellationToken))
        {
             // Double check DB inside lock
             var series = await context.MediaItems
                .AsNoTracking() // Use NoTracking to avoid conflict with other contexts? 
                // Wait, if we attach to context to return it...
                // If we want to return attached entity for navigation properties?
                // Just return disconnected or assume ID is what matters.
                .FirstOrDefaultAsync(m =>
                    m.LibraryId == library.Id &&
                    m.Type == MediaType.Series &&
                    m.Title == showName, // Exact match case sensitive? Title usually normalized?
                    cancellationToken);

             if (series == null)
             {
                 // Create new series
                 series = new MediaItem
                 {
                     LibraryId = library.Id,
                     Title = showName,
                     SortTitle = MediaStringHelpers.GetSortTitle(showName),
                     Path = Path.GetDirectoryName(episodePath) ?? episodePath,
                     Type = MediaType.Series,
                     Year = year,
                     DateModified = DateTime.UtcNow
                 };

                 context.MediaItems.Add(series);
                 await context.SaveChangesAsync(cancellationToken);
                 
                 // Queue Enrichment
                 await _metadataQueue.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV);
                 
                 _logger.LogInformation("[TvScanner] Created series: {ShowName}", showName);
             }
             else
             {
                 // Attach if needed? 
                 // If we read as no tracking, we can return it. 
                 // Episode.SeriesId = series.Id is enough.
             }
             
             return series;
        }
    }

    /// <summary>
    /// Get or create a season entity.
    /// </summary>
    private async Task<MediaItem> EnsureSeasonAsync(
        AppDbContext context,
        MediaItem series,
        int seasonNum,
        Library library,
        CancellationToken cancellationToken)
    {
        var key = $"{series.Id}-{seasonNum}";
        
        using (await LockParentAsync(key, cancellationToken))
        {
            var season = await context.MediaItems
                .AsNoTracking()
                .FirstOrDefaultAsync(m =>
                    m.SeriesId == series.Id &&
                    m.Type == MediaType.Season &&
                    m.SeasonNumber == seasonNum,
                    cancellationToken);

            if (season == null)
            {
                // Create new season
                season = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = library.Id,
                    SeriesId = series.Id,
                    Title = $"Season {seasonNum}",
                    SortTitle = $"Season {seasonNum:D2}",
                    Path = series.Path,
                    Type = MediaType.Season,
                    SeasonNumber = seasonNum,
                    DateModified = DateTime.UtcNow
                };

                // Try to get metadata from series JSON
                PopulateSeasonMetadata(season, series, seasonNum);

                context.MediaItems.Add(season);
                await context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[TvScanner] Created season: {Show} - Season {Num}", series.Title, seasonNum);
            }
            
            return season;
        }
    }

    /// <summary>
    /// Populate season metadata from series metadata JSON.
    /// </summary>
    private void PopulateSeasonMetadata(MediaItem season, MediaItem series, int seasonNum)
    {
        if (string.IsNullOrEmpty(series.MetadataJson))
            return;

        try
        {
            var seriesMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
            if (seriesMeta != null && seriesMeta.TryGetValue("seasons", out var sObj) && sObj is JsonElement sArr)
            {
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.TryGetProperty("number", out var n) && n.GetInt32() == seasonNum)
                    {
                        var meta = !string.IsNullOrEmpty(season.MetadataJson) 
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(season.MetadataJson) ?? new Dictionary<string, object>()
                            : new Dictionary<string, object>();

                        if (s.TryGetProperty("poster", out var p) && p.ValueKind != JsonValueKind.Null)
                            meta["poster"] = p.GetString() ?? string.Empty;
                        if (s.TryGetProperty("summary", out var sum) && sum.ValueKind != JsonValueKind.Null)
                        {
                            var overview = sum.GetString() ?? string.Empty;
                            meta["overview"] = overview;
                            season.Overview = overview;
                        }
                        if (s.TryGetProperty("premiereDate", out var pd) && pd.ValueKind != JsonValueKind.Null)
                            meta["premiereDate"] = pd.GetString() ?? string.Empty;

                        if (meta.Count > 0)
                            season.MetadataJson = JsonSerializer.Serialize(meta);

                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TvScanner] Failed to parse season metadata for {Show} S{Season}",
                series.Title, seasonNum);
        }
    }

    /// <summary>
    /// Get episode title from series metadata (TVMaze).
    /// </summary>
    private string? GetEpisodeTitleFromMetadata(MediaItem series, int seasonNum, int episodeNum)
    {
        if (string.IsNullOrEmpty(series.MetadataJson))
            return null;

        try
        {
            var seriesMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
            if (seriesMeta != null && 
                seriesMeta.TryGetValue("episodes", out var eObj) && 
                eObj is JsonElement eArr)
            {
                foreach (var ep in eArr.EnumerateArray())
                {
                    int s = ep.TryGetProperty("season", out var _s) ? _s.GetInt32() : 0;
                    int e = ep.TryGetProperty("episode", out var _e) ? _e.GetInt32() : 0;

                    if (s == seasonNum && e == episodeNum)
                    {
                        if (ep.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null)
                            return name.GetString();
                    }
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    /// <summary>
    /// Populate episode metadata (still, summary, airdate) from series metadata.
    /// </summary>
    private void PopulateEpisodeMetadata(MediaItem episode, MediaItem series, int seasonNum, int episodeNum)
    {
        if (string.IsNullOrEmpty(series.MetadataJson))
            return;

        try
        {
            var seriesMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
            if (seriesMeta == null || !seriesMeta.TryGetValue("episodes", out var eObj) || eObj is not JsonElement eArr)
                return;

            foreach (var ep in eArr.EnumerateArray())
            {
                int s = ep.TryGetProperty("season", out var _s) ? _s.GetInt32() : 0;
                int e = ep.TryGetProperty("episode", out var _e) ? _e.GetInt32() : 0;

                if (s == seasonNum && e == episodeNum)
                {
                    var epMeta = !string.IsNullOrEmpty(episode.MetadataJson) 
                         ? JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson) ?? new Dictionary<string, object>()
                         : new Dictionary<string, object>();

                    // Extract still image URL
                    if (ep.TryGetProperty("still", out var stillProp) && stillProp.ValueKind != JsonValueKind.Null)
                    {
                        var stillUrl = stillProp.GetString();
                        if (!string.IsNullOrEmpty(stillUrl))
                            epMeta["still"] = stillUrl;
                    }

                    // Extract summary/description
                    if (ep.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind != JsonValueKind.Null)
                    {
                        var summary = summaryProp.GetString();
                        if (!string.IsNullOrEmpty(summary))
                        {
                            epMeta["summary"] = summary;
                            episode.Overview = summary;
                        }
                    }

                    // Extract airdate
                    if (ep.TryGetProperty("airdate", out var airdateProp) && airdateProp.ValueKind != JsonValueKind.Null)
                    {
                        var airdate = airdateProp.GetString();
                        if (!string.IsNullOrEmpty(airdate))
                            epMeta["airdate"] = airdate;
                    }

                    if (epMeta.Count > 0)
                    {
                        episode.MetadataJson = JsonSerializer.Serialize(epMeta);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TvScanner] Failed to parse episode metadata for S{Season}E{Episode}",
                seasonNum, episodeNum);
        }
    }

    /// <summary>
    /// Parse TV info from directory structure.
    /// </summary>
    private (string ShowName, int Season) ParseTvInfoFromDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            return (string.Empty, 0);

        var dirName = Path.GetFileName(dir);

        // Check for "Season X" folder
        var seasonMatch = System.Text.RegularExpressions.Regex.Match(
            dirName ?? "", @"Season\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (seasonMatch.Success)
        {
            var season = int.Parse(seasonMatch.Groups[1].Value);
            var parentDir = Path.GetDirectoryName(dir);
            var showName = parentDir != null ? Path.GetFileName(parentDir) : string.Empty;
            return (showName ?? string.Empty, season);
        }

        // Otherwise, directory name is probably the show name
        return (dirName ?? string.Empty, 1);
    }

    /// <summary>
    /// Cleanup empty seasons and series.
    /// </summary>
    protected override async Task CleanupEmptyContainersAsync(
        AppDbContext context,
        Library library,
        CancellationToken cancellationToken)
    {
        // Find seasons with no episodes
        var emptySeasons = await context.MediaItems
            .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Season)
            .Where(season => !context.MediaItems.Any(ep =>
                ep.Type == MediaType.Episode && ep.SeasonId == season.Id))
            .ToListAsync(cancellationToken);

        if (emptySeasons.Count > 0)
        {
            _logger.LogInformation("[TvScanner] Removing {Count} empty seasons", emptySeasons.Count);
            context.MediaItems.RemoveRange(emptySeasons);
        }

        // Find series with no episodes
        var emptySeries = await context.MediaItems
            .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Series)
            .Where(series => !context.MediaItems.Any(ep =>
                ep.Type == MediaType.Episode && ep.SeriesId == series.Id))
            .ToListAsync(cancellationToken);

        if (emptySeries.Count > 0)
        {
            _logger.LogInformation("[TvScanner] Removing {Count} empty series", emptySeries.Count);
            context.MediaItems.RemoveRange(emptySeries);
        }
    }
}
