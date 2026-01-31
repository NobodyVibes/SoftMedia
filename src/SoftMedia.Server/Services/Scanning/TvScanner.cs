using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using System.Text.Json;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for TV show libraries. Handles Series/Season/Episode hierarchy.
/// </summary>
public class TvScanner : BaseMediaScanner
{
    private readonly IBackgroundImageCacheService _backgroundImageCache;
    private readonly IMediaProbeService _mediaProbeService;

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
        IMediaProbeService mediaProbeService)
        : base(scopeFactory, logger, notificationService)
    {
        _backgroundImageCache = backgroundImageCache;
        _mediaProbeService = mediaProbeService;
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
        // This ensures BackgroundImageCacheService sees all seasons/episodes when processing.
        foreach (var seriesId in _newSeriesIds)
        {
            _backgroundImageCache.QueueImageCaching(seriesId);
        }
    }

    /// <summary>
    /// Process a single video file as a TV episode.
    /// </summary>
    protected override async Task<ScanResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        try
        {
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

            // Ensure series exists
            var series = await EnsureSeriesAsync(context, showName, showYear, library, filePath, cancellationToken);

            // Ensure season exists
            var season = await EnsureSeasonAsync(context, series, seasonNum, library, cancellationToken);

            // Probe media for technical metadata
            var probe = await _mediaProbeService.ProbeMediaAsync(filePath);
            
            // Create or update episode
            var isNew = existing == null;
            var episode = existing ?? new MediaItem { LibraryId = library.Id };

            // Determine episode title
            var finalTitle = !string.IsNullOrEmpty(episodeTitle) 
                ? episodeTitle 
                : $"Episode {episodeNum}";

            // Try to get authoritative title from TVMaze metadata
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

            // Populate technical metadata
            if (probe != null)
            {
                episode.Duration = probe.Duration;
                episode.VideoCodec = probe.VideoCodec;
                episode.AudioCodec = probe.AudioCodec;
                episode.Resolution = probe.Resolution;
                episode.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

                // Persist technical metadata (chapters/credits)
                var meta = !string.IsNullOrEmpty(episode.MetadataJson)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson) ?? new Dictionary<string, object>()
                    : new Dictionary<string, object>();
                
                bool metaModified = false;

                if (probe.CreditsStart.HasValue)
                {
                    meta["creditsStart"] = probe.CreditsStart.Value;
                    metaModified = true;
                }

                if (probe.Chapters != null && probe.Chapters.Count > 0)
                {
                    // Serialize as anonymous objects to get camelCase keys expected by DTO
                    var chaptersList = probe.Chapters.Select(c => new { startTime = c.StartTime, title = c.Title }).ToList();
                    meta["chapters"] = chaptersList;
                    metaModified = true;
                }

                if (metaModified)
                {
                    episode.MetadataJson = JsonSerializer.Serialize(meta);
                }
            }

            // Populate episode metadata from series (still image, summary, airdate)
            PopulateEpisodeMetadata(episode, series, seasonNum, episodeNum);

            if (isNew)
            {
                context.MediaItems.Add(episode);
                _logger.LogDebug("[TvScanner] Added episode: {Show} S{Season}E{Episode} - {Title}",
                    showName, seasonNum, episodeNum, finalTitle);
                return ScanResult.New;
            }
            else
            {
                _logger.LogDebug("[TvScanner] Updated episode: {Show} S{Season}E{Episode}",
                    showName, seasonNum, episodeNum);
                return ScanResult.Updated;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TvScanner] Error processing file: {FilePath}", filePath);
            return ScanResult.Skipped;
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
        // Check session cache first
        if (_seriesCache.TryGetValue(showName, out var cached))
            return cached;

        // Check database
        var series = await context.MediaItems
            .FirstOrDefaultAsync(m =>
                m.LibraryId == library.Id &&
                m.Type == MediaType.Series &&
                m.Title.ToLower() == showName.ToLower(),
                cancellationToken);

        if (series != null)
        {
            _seriesCache[showName] = series;
            return series;
        }

        // Create new series
        series = new MediaItem
        {
            LibraryId = library.Id,
            Title = showName,
            SortTitle = SortableTitle(showName),
            Path = Path.GetDirectoryName(episodePath) ?? episodePath,
            Type = MediaType.Series,
            Year = year,
            DateModified = DateTime.UtcNow
        };

        // Enrich with TVMaze metadata
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var metadataAggregator = scope.ServiceProvider.GetService<MetadataAggregator>();
            if (metadataAggregator != null)
            {
                await metadataAggregator.EnrichMediaItemAsync(series, LibraryType.TV, deferImageCaching: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TvScanner] Failed to enrich series metadata: {Show}", showName);
        }

        context.MediaItems.Add(series);
        await context.SaveChangesAsync(cancellationToken);

        _seriesCache[showName] = series;
        _newSeriesIds.Add(series.Id);  // Defer image caching until scan completes

        _logger.LogInformation("[TvScanner] Created series: {ShowName}", showName);
        return series;
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
        var cacheKey = (series.Id, seasonNum);
        if (_seasonCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Check database
        var season = await context.MediaItems
            .FirstOrDefaultAsync(m =>
                m.SeriesId == series.Id &&
                m.Type == MediaType.Season &&
                m.SeasonNumber == seasonNum,
                cancellationToken);

        if (season != null)
        {
            _seasonCache[cacheKey] = season;
            return season;
        }

        // Create new season
        season = new MediaItem
        {
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

        _seasonCache[cacheKey] = season;

        _logger.LogInformation("[TvScanner] Created season: {Show} - Season {Num}", series.Title, seasonNum);
        return season;
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

    /// <summary>
    /// Create a sortable version of a title.
    /// </summary>
    private static string SortableTitle(string title)
    {
        if (title.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            return title[4..];
        if (title.StartsWith("A ", StringComparison.OrdinalIgnoreCase))
            return title[2..];
        if (title.StartsWith("An ", StringComparison.OrdinalIgnoreCase))
            return title[3..];
        return title;
    }
}
