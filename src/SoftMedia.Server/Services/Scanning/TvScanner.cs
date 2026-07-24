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

    // Session caches — ConcurrentDictionary for thread-safe access during parallel scanning.
    // SR-WI-034: series are keyed by (CleanShowName → Year) so "Doctor Who (1963)" and
    // "Doctor Who (2005)" stay separate. Year 0 = unknown year (Year is always >= 1900).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, MediaItem>> _seriesCache = new(StringComparer.OrdinalIgnoreCase);
    private const int UnknownYearKey = 0;
    private readonly ConcurrentDictionary<(Guid SeriesId, int SeasonNum), MediaItem> _seasonCache = new();

    // SR-WI-030: true only while a full library scan is running. Series/seasons created
    // during a scan defer enrichment to the post-scan drain; series/seasons created outside
    // one (the watcher's ProcessSingleFileAsync path, e.g. a Sonarr import) enqueue metadata
    // immediately — there is no post-scan drain on that path, so deferring meant no metadata
    // until the next full scan.
    private volatile bool _fullScanActive;
    
    // Track series IDs that need metadata enrichment after the scan completes.
    // Deferred to post-scan so all seasons/episodes exist in the DB before
    // FilterToLocalEpisodesAsync runs, preventing image loss for later-discovered seasons.
    private readonly ConcurrentDictionary<Guid, byte> _seriesNeedingEnrichment = new();

    // Cache parsed series ProviderMetadataCache (TVMaze payloads) to avoid O(N) re-parsing per episode
    private readonly ConcurrentDictionary<Guid, Dictionary<string, object>?> _parsedSeriesMetadataCache = new();

    /// <summary>
    /// Test seam (project convention; no InternalsVisibleTo): seeds the parsed TVMaze
    /// payload cache that <see cref="ScanLibraryAsync"/> normally pre-loads from the DB.
    /// </summary>
    protected void SeedParsedSeriesMetadata(Guid seriesId, Dictionary<string, object>? metadata)
        => _parsedSeriesMetadataCache[seriesId] = metadata;



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
        _fullScanActive = true;

        // Bulk pre-load all existing Series for this library
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingSeries = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Series)
                .ToListAsync(cancellationToken);

            foreach (var s in existingSeries)
                CacheSeries(s);

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

        try
        {
            await base.ScanLibraryAsync(library, progress, cancellationToken);

            // R-WI-014: post-scan local-artwork sweep over every series seen this scan (deferred,
            // like enrichment below, because cached series entities are detached from the per-file
            // contexts). Fresh-loads each series, applies poster.jpg/folder.jpg/fanart sidecars
            // from the series folder, and forces a re-enqueue when a local poster was removed or a
            // local-postered series hasn't had its one enrichment pass yet (TvScanner otherwise
            // only enqueues brand-new series). Failure-isolated: artwork trouble must never stop
            // the deferred enrichment drain below.
            try
            {
                await ApplyLocalSeriesArtworkAsync(library, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TvScanner] Local artwork sweep failed; continuing to enrichment enqueue.");
            }

            // Deferred metadata enrichment: enqueue ALL series that need enrichment AFTER
            // the scan completes. This guarantees every season and episode exists in the DB
            // before FilterToLocalEpisodesAsync runs, so no season images are lost.
            _logger.LogInformation("[TvScanner] Enqueueing deferred metadata enrichment for {Count} series", _seriesNeedingEnrichment.Count);
            foreach (var seriesId in _seriesNeedingEnrichment.Keys)
            {
                await _metadataQueue.EnqueueMetadataRefreshAsync(seriesId, LibraryType.TV, libraryId: library.Id);
            }
        }
        finally
        {
            _fullScanActive = false;
        }
    }

    /// <summary>All series cached this session, across every (title, year) bucket.</summary>
    private IEnumerable<MediaItem> CachedSeries => _seriesCache.Values.SelectMany(byYear => byYear.Values);

    /// <summary>
    /// SR-WI-034 identity lookup. An incoming concrete year matches that exact year first,
    /// then falls back to a same-title series with unknown year. A null incoming year is a
    /// wildcard: it matches an existing same-title series regardless of year rather than
    /// creating a duplicate — the conservative choice (no automatic split of merged rows).
    /// </summary>
    private bool TryGetCachedSeries(string title, int? year, out MediaItem series)
    {
        series = null!;
        if (!_seriesCache.TryGetValue(title, out var byYear) || byYear.IsEmpty)
            return false;

        if (year.HasValue)
        {
            if (byYear.TryGetValue(year.Value, out series!)) return true;
            return byYear.TryGetValue(UnknownYearKey, out series!);
        }

        foreach (var kv in byYear)
        {
            series = kv.Value;
            return true;
        }
        return false;
    }

    private void CacheSeries(MediaItem series)
    {
        var byYear = _seriesCache.GetOrAdd(series.Title, _ => new ConcurrentDictionary<int, MediaItem>());
        byYear.TryAdd(series.Year ?? UnknownYearKey, series);
    }

    /// <summary>
    /// Sweeps the series folder for local artwork sidecars. Series.Path may be a SEASON
    /// subfolder (it's the first-discovered episode's directory), so when it looks like one
    /// ("Season 01", "Specials", "S1"…) the parent folder is swept as the series root instead.
    /// Never sweeps above that (a poster.jpg in the library root belongs to nothing).
    /// </summary>
    private async Task ApplyLocalSeriesArtworkAsync(Library library, CancellationToken cancellationToken)
    {
        var seriesIds = CachedSeries.Select(s => s.Id).Distinct().ToList();
        if (seriesIds.Count == 0) return;

        // A poster.jpg in the LIBRARY ROOT belongs to no particular series — never sweep roots
        // (also neutralises the season-folder heuristic's parent-walk landing on a root when a
        // show is literally named "Specials"/"S10" directly under it).
        var libraryRoots = library.Paths
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var localArtwork = scope.ServiceProvider.GetRequiredService<ILocalArtworkService>();

        // Resolve each series' sweep folder up front so folders CLAIMED BY MULTIPLE SERIES can
        // be skipped — a bare poster.jpg in a shared folder belongs to no one (verifier: the TV
        // analog of the flat-multi-movie bug).
        var resolvedFolders = new Dictionary<Guid, string>();
        foreach (var s in CachedSeries.DistinctBy(s => s.Id))
        {
            if (string.IsNullOrEmpty(s.Path)) continue;
            var f = s.Path;
            var name = Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (LooksLikeSeasonFolder(name))
            {
                f = Path.GetDirectoryName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? f;
            }
            resolvedFolders[s.Id] = Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        var folderClaims = resolvedFolders.Values
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var seriesId in seriesIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!resolvedFolders.TryGetValue(seriesId, out var folder)) continue;
                if (libraryRoots.Contains(folder)) continue;
                if (folderClaims.GetValueOrDefault(folder) > 1) continue; // shared folder → ambiguous art

                var series = await context.MediaItems.FirstOrDefaultAsync(m => m.Id == seriesId, cancellationToken);
                if (series == null) continue;

                var artwork = await localArtwork.ApplyLocalArtworkAsync(series, folder, fileStem: null);
                if (artwork.Changed)
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                // Re-enqueue for the one-pass enrichment — but honour retry exhaustion like the
                // policy does, or an unmatchable series would retry on every scan forever.
                if ((artwork.LocalPosterRemoved ||
                     (series.PosterFromLocalFile && string.IsNullOrEmpty(series.MetadataHash)))
                    && !series.IsRetryExhausted)
                {
                    _seriesNeedingEnrichment.TryAdd(seriesId, 0);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One unreadable folder / transient DB error must not abort the rest of the sweep.
                // Clear the tracker so a poisoned entity (failed SaveChanges) can't re-fail every
                // subsequent series' save on this shared context.
                _logger.LogWarning(ex, "[TvScanner] Local artwork sweep failed for series {SeriesId}; continuing.", seriesId);
                context.ChangeTracker.Clear();
            }
        }
    }

    private static bool LooksLikeSeasonFolder(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            folderName, @"^(season[ ._-]*\d+|specials?|s\d{1,2})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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

            // SR-WI-034: extract the year BEFORE cleaning — CleanShowName strips a trailing
            // year ("Doctor Who 2005" -> "Doctor Who"), so extracting afterwards always
            // returned null and same-title shows from different years merged.
            var showYear = FileNameParser.ExtractYear(showName);

            // Clean the show name
            var cleanedShowName = FileNameParser.CleanShowName(showName);
            if (!string.IsNullOrEmpty(cleanedShowName))
                showName = cleanedShowName;

            // Ensure series exists (Thread Safe)
            var series = await EnsureSeriesAsync(context, showName, showYear, library, filePath, cancellationToken);

            // Ensure season exists (Thread Safe)
            var season = await EnsureSeasonAsync(context, series, seasonNum, library, cancellationToken);

            // Create or update episode. Capture whether the file actually changed BEFORE
            // stamping Size/DateModified — the analysis strategy compares DateModified
            // against the file's mtime, so stamping first silently disabled re-probes
            // of modified files.
            var isNew = existing == null;
            var fileChanged = existing != null
                && (existing.Size != file.Size || existing.DateModified != file.LastWriteUtc);
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

            // Delegate technical analysis to MediaAnalysisService (Smart Probe).
            // Changed files get a full re-probe; unchanged files stay in Missing mode,
            // which only probes when technical data is absent.
            var refreshMode = isNew || fileChanged ? MetadataRefreshMode.Full : MetadataRefreshMode.Missing;
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
                // Unchanged file = skipped, so scan counters reflect reality. Metadata
                // propagated from the cached series payload above still gets saved either way.
                _logger.LogDebug("[TvScanner] {Action} episode: {Show} S{Season}E{Episode}",
                    fileChanged ? "Updated" : "Skipped", showName, seasonNum, episodeNum);
                return new ScanOperationResult(
                    fileChanged ? ScanResult.Updated : ScanResult.Skipped, episode.Id, EnqueueMetadata: false);
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
    /// falling back to a DB double-check + lock before creating a new series.
    /// SR-WI-030: the DB double-check matters on the watcher single-file path, which runs
    /// on a scanner whose session cache was never pre-loaded — without it every watcher
    /// import (e.g. Sonarr) minted a duplicate Series row.
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
        if (TryGetCachedSeries(showName, year, out var cached))
            return cached;

        // Slow path: cache miss — acquire lock, double-check DB, then create
        using (await LockParentAsync(showName, cancellationToken))
        {
            // Double-check cache after acquiring lock (another thread may have created it)
            if (TryGetCachedSeries(showName, year, out cached))
                return cached;

            // Double-check against DB (mirrors BookScanner.EnsureComicSeriesAsync): the cache
            // is per-scanner-session, but the series may already exist from a previous scan.
            // Same identity as the cache: (title, year), null year = wildcard.
            var lowered = showName.ToLowerInvariant();
            var candidates = await context.MediaItems
                .Where(m => m.LibraryId == library.Id
                         && m.Type == MediaType.Series
                         && m.Title.ToLower() == lowered)
                .ToListAsync(cancellationToken);
            var dbSeries = year.HasValue
                ? candidates.FirstOrDefault(c => c.Year == year.Value) ?? candidates.FirstOrDefault(c => c.Year == null)
                : candidates.FirstOrDefault();
            if (dbSeries != null)
            {
                CacheSeries(dbSeries);
                return dbSeries;
            }

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

            // SR-WI-035: this save runs inside the parallel directory walk — route it
            // through the shared scanner write lock like the base class's end-of-directory
            // saves, or concurrent writers hit SQLITE_BUSY on first big scans.
            await _dbWriteLock.WaitAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _dbWriteLock.Release();
            }

            // Add to cache for subsequent lookups
            CacheSeries(series);

            // SR-WI-030: during a full scan, defer enrichment to the post-scan drain; on the
            // watcher single-file path there is no drain, so enqueue immediately.
            if (_fullScanActive)
                _seriesNeedingEnrichment.TryAdd(series.Id, 0);
            else
                await _metadataQueue.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV, libraryId: library.Id);

            _logger.LogInformation("[TvScanner] Created series: {ShowName} ({Year})", showName, year?.ToString() ?? "unknown year");
            return series;
        }
    }

    /// <summary>
    /// Get or create a season entity. Uses pre-loaded cache for O(1) lookups,
    /// falling back to a DB double-check + lock before creating a new season.
    /// SR-WI-030: the DB double-check protects the watcher single-file path, whose
    /// session cache is never pre-loaded (see <see cref="EnsureSeriesAsync"/>).
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

        // Slow path: cache miss — acquire lock, double-check DB, then create
        var lockKey = $"{series.Id}-{seasonNum}";
        using (await LockParentAsync(lockKey, cancellationToken))
        {
            // Double-check cache after acquiring lock
            if (_seasonCache.TryGetValue(cacheKey, out cached))
                return cached;

            // Double-check against DB — the season may already exist from a previous scan
            // (same identity as the cache key: SeriesId + SeasonNumber).
            var dbSeason = await context.MediaItems
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id
                                       && m.Type == MediaType.Season
                                       && m.SeasonNumber == seasonNum,
                    cancellationToken);
            if (dbSeason != null)
            {
                _seasonCache.TryAdd(cacheKey, dbSeason);
                return dbSeason;
            }

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

            // SR-WI-035: parallel-walk save — must hold the shared scanner write lock
            // (see EnsureSeriesAsync).
            await _dbWriteLock.WaitAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _dbWriteLock.Release();
            }

            // Add to cache for subsequent lookups
            _seasonCache.TryAdd(cacheKey, season);

            // New season discovered — the series needs (re-)enrichment so
            // FilterToLocalEpisodesAsync includes this season's images. During a full scan
            // that's deferred to the post-scan drain; on the watcher path enqueue now.
            if (_fullScanActive)
                _seriesNeedingEnrichment.TryAdd(series.Id, 0);
            else
                await _metadataQueue.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV, libraryId: library.Id);

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
                        // SR-WI-031: these are fill-only. Enrichment (TvMetadataEnricher /
                        // ImageDownloadQueueService) owns updates to already-populated fields;
                        // re-stamping them on every scan clobbered locally cached values.

                        // Summary — only when missing (enrichment propagation owns refreshes)
                        if (string.IsNullOrEmpty(episode.Overview)
                            && ep.TryGetProperty("summary", out var summary))
                        {
                            var summaryText = summary.GetString();
                            if (!string.IsNullOrEmpty(summaryText))
                                episode.Overview = System.Text.RegularExpressions.Regex.Replace(summaryText, "<.*?>", "");
                        }

                        // Air date — only when missing
                        if (episode.ReleaseDate == null
                            && ep.TryGetProperty("airdate", out var airdate) && DateTime.TryParse(airdate.GetString(), out var date))
                            episode.ReleaseDate = date;

                        // Still image -> Backdrop (promoted column). Never overwrite a locally
                        // cached still (/cache/images/...) written by ImageDownloadQueueService
                        // with the remote TVMaze URL — that re-broke offline art on every scan.
                        var hasLocalBackdrop = !string.IsNullOrEmpty(episode.BackdropUrl)
                            && episode.BackdropUrl.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase);
                        if (!hasLocalBackdrop
                            && ep.TryGetProperty("image", out var img) && img.ValueKind != JsonValueKind.Null)
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
    /// SR-WI-033: season-like folders ("Season X", "Specials", "S01", "Season 0") are
    /// recognized via the same heuristic as the artwork sweep (<see cref="LooksLikeSeasonFolder"/>),
    /// so a file in "Show\Specials\" resolves to season 0 of "Show" instead of creating a
    /// series literally named "Specials".
    /// </summary>
    private (string ShowName, int Season) ParseTvInfoFromDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            return (string.Empty, 0);

        var dirName = Path.GetFileName(dir);

        if (TryParseSeasonFolder(dirName, out var season))
        {
            // Immediate parent is a season-like folder — the show name lives one level higher.
            var parentDir = Path.GetDirectoryName(dir);
            var showName = parentDir != null ? Path.GetFileName(parentDir) : string.Empty;
            return (showName ?? string.Empty, season);
        }

        // Otherwise, directory name is probably the show name
        return (dirName ?? string.Empty, 1);
    }

    /// <summary>
    /// Aligned with <see cref="LooksLikeSeasonFolder"/>: "Season 5"/"S05" parse to that
    /// season number; "Specials", "Season 0" and "Season 00" parse to season 0. Falls back
    /// to the historical non-anchored "Season X" match (e.g. "Season 3 - Extended").
    /// </summary>
    private static bool TryParseSeasonFolder(string? folderName, out int season)
    {
        season = 0;
        if (string.IsNullOrEmpty(folderName)) return false;

        if (LooksLikeSeasonFolder(folderName))
        {
            var digits = System.Text.RegularExpressions.Regex.Match(folderName, @"\d+");
            season = digits.Success ? int.Parse(digits.Value) : 0; // "Specials" carries no digits -> season 0
            return true;
        }

        var seasonMatch = System.Text.RegularExpressions.Regex.Match(
            folderName, @"Season\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (seasonMatch.Success)
        {
            season = int.Parse(seasonMatch.Groups[1].Value);
            return true;
        }

        return false;
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
