using System.Collections.Concurrent;
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
    private readonly IMediaAnalysisService _mediaAnalysisService;

    public override LibraryType SupportedType => LibraryType.TV;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Video;
    public override string DisplayName => "TV Scanner";

    // Session caches — ConcurrentDictionary for thread-safe access during parallel scanning
    private readonly ConcurrentDictionary<string, MediaItem> _seriesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Guid SeriesId, int SeasonNum), MediaItem> _seasonCache = new();
    
    // Track new series IDs for deferred image caching (to avoid race condition)
    private readonly ConcurrentDictionary<Guid, byte> _newSeriesIds = new();

    // Cache parsed series MetadataJson to avoid O(N) re-parsing per episode
    private readonly ConcurrentDictionary<Guid, Dictionary<string, object>?> _parsedSeriesMetadataCache = new();



    public TvScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<TvScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
    }

    /// <summary>
    /// Override to pre-load session caches before the parallel directory loop.
    /// Bulk-loads all existing Series and Season items for this library in one query each,
    /// eliminating the N+1 pattern where each file would trigger a separate DB lookup.
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
        _parsedSeriesMetadataCache.Clear();

        // Bulk pre-load all existing Series for this library
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingSeries = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Series)
                .ToListAsync(cancellationToken);

            foreach (var s in existingSeries)
                _seriesCache.TryAdd(s.Title, s);

            var existingSeasons = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Season)
                .ToListAsync(cancellationToken);

            foreach (var s in existingSeasons)
            {
                if (s.SeriesId.HasValue)
                    _seasonCache.TryAdd((s.SeriesId.Value, s.SeasonNumber ?? 0), s);
            }

            _logger.LogInformation("[TvScanner] Pre-loaded {SeriesCount} series and {SeasonCount} seasons for library {LibraryId}",
                existingSeries.Count, existingSeasons.Count, library.Id);
        }

        await base.ScanLibraryAsync(library, progress, cancellationToken);
    }


    /// <summary>
    /// Process a single video file as a TV episode.
    /// </summary>
    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
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
            episode.Size = file.Size;
            episode.DateModified = file.LastWriteUtc;

            // Delegate technical analysis to MediaAnalysisService (Smart Probe)
            var refreshMode = isNew ? MetadataRefreshMode.Full : MetadataRefreshMode.Missing;
            await _mediaAnalysisService.AnalyzeAsync(episode, filePath, refreshMode, cancellationToken);

            // Populate episode metadata from series (still image, summary, airdate)
            PopulateEpisodeMetadata(episode, series, seasonNum, episodeNum);

            if (isNew)
            {
                context.MediaItems.Add(episode);

                _logger.LogDebug("[TvScanner] Added episode: {Show} S{Season}E{Episode} - {Title}",
                    showName, seasonNum, episodeNum, finalTitle);
                // Episodes never need individual metadata enrichment — TVMaze returns all
                // episode data with the series fetch, and MetadataAggregator propagates it down.
                return new ScanOperationResult(ScanResult.New, episode.Id, EnqueueMetadata: false);
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
    /// Get or create a series entity. Uses pre-loaded cache for O(1) lookups,
    /// falling back to DB + lock only when creating new series.
    /// </summary>
    private async Task<MediaItem> EnsureSeriesAsync(
        AppDbContext context,
        string showName,
        int? year,
        Library library,
        string episodePath,
        CancellationToken cancellationToken)
    {
        // Fast path: check pre-loaded cache (thread-safe ConcurrentDictionary)
        if (_seriesCache.TryGetValue(showName, out var cached))
            return cached;

        // Slow path: cache miss — acquire lock and create new series
        using (await LockParentAsync(showName, cancellationToken))
        {
            // Double-check cache after acquiring lock (another thread may have created it)
            if (_seriesCache.TryGetValue(showName, out cached))
                return cached;

            // Create new series
            var series = new MediaItem
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

            // Queue metadata enrichment
            await _metadataQueue.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV);

            // Add to cache for subsequent lookups
            _seriesCache.TryAdd(showName, series);
            _newSeriesIds.TryAdd(series.Id, 0);

            _logger.LogInformation("[TvScanner] Created series: {ShowName}", showName);
            return series;
        }
    }

    /// <summary>
    /// Get or create a season entity. Uses pre-loaded cache for O(1) lookups,
    /// falling back to DB + lock only when creating new seasons.
    /// </summary>
    private async Task<MediaItem> EnsureSeasonAsync(
        AppDbContext context,
        MediaItem series,
        int seasonNum,
        Library library,
        CancellationToken cancellationToken)
    {
        var cacheKey = (series.Id, seasonNum);

        // Fast path: check pre-loaded cache
        if (_seasonCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Slow path: cache miss — acquire lock and create new season
        var lockKey = $"{series.Id}-{seasonNum}";
        using (await LockParentAsync(lockKey, cancellationToken))
        {
            // Double-check cache after acquiring lock
            if (_seasonCache.TryGetValue(cacheKey, out cached))
                return cached;

            // Create new season
            var season = new MediaItem
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

            // Add to cache for subsequent lookups
            _seasonCache.TryAdd(cacheKey, season);

            _logger.LogInformation("[TvScanner] Created season: {Show} - Season {Num}", series.Title, seasonNum);
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
            var seriesMeta = MetadataJsonHelper.Parse(series.MetadataJson);
            if (seriesMeta != null && seriesMeta.TryGetValue("seasons", out var sObj) && sObj is JsonElement sArr)
            {
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.TryGetProperty("number", out var n) && n.GetInt32() == seasonNum)
                    {
                        var meta = !string.IsNullOrEmpty(season.MetadataJson) 
                            ? MetadataJsonHelper.Parse(season.MetadataJson)
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

    // Cache parsed series MetadataJson to avoid O(N) re-parsing per episode
    private readonly ConcurrentDictionary<Guid, Dictionary<(int season, int episode), JsonElement>?> _parsedEpisodeCache = new();

    /// <summary>
    /// Get or parse the series episodes into an O(1) lookup dictionary.
    /// Uses a per-scan cache to avoid redundant parsing for every episode.
    /// </summary>
    private Dictionary<(int season, int episode), JsonElement>? GetCachedEpisodes(MediaItem series)
    {
        return _parsedEpisodeCache.GetOrAdd(series.Id, _ =>
        {
            if (string.IsNullOrEmpty(series.MetadataJson))
                return null;
            try
            {
                var parsedMeta = MetadataJsonHelper.Parse(series.MetadataJson);
                var dict = new Dictionary<(int season, int episode), JsonElement>();

                if (parsedMeta != null && parsedMeta.TryGetValue("episodes", out var eObj) && eObj is JsonElement eArr)
                {
                    foreach (var ep in eArr.EnumerateArray())
                    {
                        int s = ep.TryGetProperty("season", out var _s) ? _s.GetInt32() : 0;
                        int e = ep.TryGetProperty("episode", out var _e) ? _e.GetInt32() : 0;
                        dict[(s, e)] = ep;
                    }
                }
                return dict;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Get episode title from series metadata (TVMaze).
    /// Uses cached O(1) lookup to avoid re-parsing and linear scans per episode.
    /// </summary>
    private string? GetEpisodeTitleFromMetadata(MediaItem series, int seasonNum, int episodeNum)
    {
        var episodes = GetCachedEpisodes(series);
        if (episodes == null)
            return null;

        if (episodes.TryGetValue((seasonNum, episodeNum), out var ep))
        {
            if (ep.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null)
            {
                return name.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Populate episode metadata (still, summary, airdate) from series metadata.
    /// Uses cached O(1) lookup to avoid re-parsing and linear scans per episode.
    /// </summary>
    private void PopulateEpisodeMetadata(MediaItem episode, MediaItem series, int seasonNum, int episodeNum)
    {
        var episodes = GetCachedEpisodes(series);
        if (episodes == null)
            return;

        try
        {
            if (episodes.TryGetValue((seasonNum, episodeNum), out var ep))
            {
                var epMeta = !string.IsNullOrEmpty(episode.MetadataJson) 
                     ? MetadataJsonHelper.Parse(episode.MetadataJson)
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
