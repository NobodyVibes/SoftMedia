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
    
    // Track series IDs that need metadata enrichment after the scan completes.
    // Deferred to post-scan so all seasons/episodes exist in the DB before
    // FilterToLocalEpisodesAsync runs, preventing image loss for later-discovered seasons.
    private readonly ConcurrentDictionary<Guid, byte> _seriesNeedingEnrichment = new();

    // Cache parsed series ProviderMetadataCache (TVMaze payloads) to avoid O(N) re-parsing per episode
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
        _seriesNeedingEnrichment.Clear();
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

            // Bulk pre-load Provider Metadata Caches (TVMaze payloads) for the series in this library
            var seriesIds = existingSeries.Select(s => s.Id).ToList();
            var caches = await context.ProviderMetadataCaches
                .AsNoTracking()
                .Where(c => seriesIds.Contains(c.MediaItemId) && c.ProviderId == "TVMaze")
                .ToListAsync(cancellationToken);

            foreach (var cache in caches)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(cache.RawPayload);
                    if (parsed != null)
                        _parsedSeriesMetadataCache.TryAdd(cache.MediaItemId, parsed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TvScanner] Failed to parse cached metadata for series {Id}", cache.MediaItemId);
                }
            }

            _logger.LogInformation("[TvScanner] Pre-loaded series: {SeriesCount}, seasons: {SeasonCount}, caches: {CacheCount} for library: {LibraryId}",
                existingSeries.Count, existingSeasons.Count, caches.Count, library.Id);
        }

        await base.ScanLibraryAsync(library, progress, cancellationToken);

        // Deferred metadata enrichment: enqueue ALL series that need enrichment AFTER
        // the scan completes. This guarantees every season and episode exists in the DB
        // before FilterToLocalEpisodesAsync runs, so no season images are lost.
        _logger.LogInformation("[TvScanner] Enqueueing deferred metadata enrichment for {Count} series", _seriesNeedingEnrichment.Count);
        foreach (var seriesId in _seriesNeedingEnrichment.Keys)
        {
            await _metadataQueue.EnqueueMetadataRefreshAsync(seriesId, LibraryType.TV);
        }
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

            // Add to cache for subsequent lookups
            _seriesCache.TryAdd(showName, series);

            // Mark for deferred metadata enrichment (runs after scan completes)
            _seriesNeedingEnrichment.TryAdd(series.Id, 0);

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

            // New season discovered — mark series for deferred enrichment so
            // FilterToLocalEpisodesAsync includes this season's images.
            _seriesNeedingEnrichment.TryAdd(series.Id, 0);

            _logger.LogInformation("[TvScanner] Created season: {Show} - Season {Num}", series.Title, seasonNum);
            return season;
        }
    }

    /// <summary>
    /// Populate season metadata from series metadata.
    /// Now a no-op: TvMetadataEnricher propagates metadata to seasons/episodes
    /// via promoted columns during enrichment in MetadataAggregator.
    /// </summary>
    private void PopulateSeasonMetadata(MediaItem season, MediaItem series, int seasonNum)
    {
        // Season metadata (poster, overview, premiere date) is now written to promoted
        // columns by TvMetadataEnricher.PropagateEpisodeMetadataAsync() during enrichment.
        // No action needed during scanning.
    }

    /// <summary>
    /// Get episode title from previously-enriched episode data.
    /// Since TvMetadataEnricher writes titles directly to episode.Title during enrichment,
    /// existing episodes already have correct titles in the DB.
    /// Returns null for new episodes (titles come from enrichment post-scan).
    /// </summary>
    private string? GetEpisodeTitleFromMetadata(MediaItem series, int seasonNum, int episodeNum)
    {
        if (!_parsedSeriesMetadataCache.TryGetValue(series.Id, out var metadata) || metadata == null)
            return null;

        // The TVMaze JSON contains an "episodes" array in the "_embedded" property
        if (metadata.TryGetValue("_embedded", out var embeddedObj) && embeddedObj is JsonElement embedded)
        {
            if (embedded.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in episodes.EnumerateArray())
                {
                    if (ep.TryGetProperty("season", out var s) && s.GetInt32() == seasonNum &&
                        ep.TryGetProperty("number", out var n) && n.GetInt32() == episodeNum)
                    {
                        if (ep.TryGetProperty("name", out var name))
                            return name.GetString();
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Populate episode metadata from series metadata.
    /// Now a no-op: TvMetadataEnricher propagates metadata to promoted columns
    /// during enrichment in MetadataAggregator.
    /// </summary>
    private void PopulateEpisodeMetadata(MediaItem episode, MediaItem series, int seasonNum, int episodeNum)
    {
        if (!_parsedSeriesMetadataCache.TryGetValue(series.Id, out var metadata) || metadata == null)
            return;

        if (metadata.TryGetValue("_embedded", out var embeddedObj) && embeddedObj is JsonElement embedded)
        {
            if (embedded.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in episodes.EnumerateArray())
                {
                    if (ep.TryGetProperty("season", out var s) && s.GetInt32() == seasonNum &&
                        ep.TryGetProperty("number", out var n) && n.GetInt32() == episodeNum)
                    {
                        // Summary
                        if (ep.TryGetProperty("summary", out var summary))
                        {
                            var summaryText = summary.GetString();
                            if (!string.IsNullOrEmpty(summaryText))
                                episode.Overview = System.Text.RegularExpressions.Regex.Replace(summaryText, "<.*?>", "");
                        }

                        // Air date
                        if (ep.TryGetProperty("airdate", out var airdate) && DateTime.TryParse(airdate.GetString(), out var date))
                            episode.ReleaseDate = date;

                        // Still image -> Backdrop (promoted column)
                        if (ep.TryGetProperty("image", out var img) && img.ValueKind != JsonValueKind.Null)
                        {
                             if (img.TryGetProperty("original", out var original))
                                 episode.BackdropUrl = original.GetString();
                             else if (img.TryGetProperty("medium", out var medium))
                                 episode.BackdropUrl = medium.GetString();
                        }
                        
                        break;
                    }
                }
            }
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
