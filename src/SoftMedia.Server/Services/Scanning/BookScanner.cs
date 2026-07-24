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

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for book libraries. Flat books (.pdf/.epub/.mobi/etc.) are cataloged as
/// standalone Book MediaItems. Comic archives (.cbz/.cbr) are grouped by series —
/// a ComicSeries parent with per-file ComicIssue children, mirroring the TV
/// Series/Episode hierarchy so the existing UX patterns can be reused.
/// </summary>
public class BookScanner : BaseMediaScanner
{
    private readonly IMediaAnalysisService _mediaAnalysisService;
    private readonly IBookMetadataExtractor _bookMetadataExtractor;

    public override LibraryType SupportedType => LibraryType.Book;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Book;
    public override string DisplayName => "Book Scanner";

    private static readonly HashSet<string> ComicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".cbr"
    };

    // Session cache of ComicSeries parents keyed by (LibraryId, SeriesName).
    // Rebuilt at the start of every scan to avoid stale data.
    private readonly ConcurrentDictionary<(Guid LibraryId, string Series), MediaItem> _seriesCache =
        new(new SeriesCacheKeyComparer());

    // Tracks newly-created ComicSeries IDs so they get metadata enrichment enqueued
    // after the scan completes (mirrors TvScanner's deferred-enrichment pattern — we
    // can't enqueue during creation because the router needs the series + issues to
    // exist in the DB together).
    private readonly ConcurrentDictionary<Guid, byte> _seriesNeedingEnrichment = new();

    public BookScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<BookScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue,
        IBookMetadataExtractor bookMetadataExtractor)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
        _bookMetadataExtractor = bookMetadataExtractor;
    }

    /// <summary>
    /// Pre-load existing ComicSeries parents so per-file lookups are O(1).
    /// </summary>
    public override async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _seriesCache.Clear();
        _seriesNeedingEnrichment.Clear();

        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existingSeries = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.ComicSeries)
                .ToListAsync(cancellationToken);

            foreach (var s in existingSeries)
                _seriesCache.TryAdd((library.Id, s.Title), s);

            if (existingSeries.Count > 0)
            {
                _logger.LogInformation("[BookScanner] Pre-loaded {Count} comic series for library {LibraryId}",
                    existingSeries.Count, library.Id);
            }
        }

        await base.ScanLibraryAsync(library, progress, cancellationToken);

        // Deferred enrichment: enqueue newly-created ComicSeries after all issues have
        // been persisted so the router's ComicInfo primary can locate the first-issue
        // file for series-level fields.
        if (_seriesNeedingEnrichment.Count > 0)
        {
            _logger.LogInformation("[BookScanner] Enqueueing metadata enrichment for {Count} new comic series",
                _seriesNeedingEnrichment.Count);
            foreach (var seriesId in _seriesNeedingEnrichment.Keys)
            {
                await _metadataQueue.EnqueueMetadataRefreshAsync(seriesId, LibraryType.Book, libraryId: library.Id);
            }
        }
    }

    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(file.Path);
        if (ComicExtensions.Contains(ext))
        {
            return await ProcessComicFileAsync(context, file, existing, library, cancellationToken);
        }
        return await ProcessFlatBookFileAsync(context, file, existing, library, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────── Flat book pipeline

    private async Task<ScanOperationResult> ProcessFlatBookFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            // Fast path: unchanged file (same size + mtime) needs no re-extraction —
            // embedded metadata can't have changed, and opening every EPUB/PDF on every
            // rescan was the dominant cost of scanning a stable book library. Enrichment
            // policy still runs so incomplete items keep getting queued.
            if (existing != null && existing.Size == file.Size && existing.DateModified == file.LastWriteUtc)
            {
                if (MetadataEnrichmentPolicy.NeedsEnrichment(existing, _strictEnrichment))
                {
                    _logger.LogDebug("[BookScanner] Queued metadata enrichment (unchanged file): {Path}", filePath);
                    return new ScanOperationResult(ScanResult.Updated, existing.Id, EnqueueMetadata: true);
                }
                return new ScanOperationResult(ScanResult.Skipped, existing.Id, EnqueueMetadata: false);
            }

            // Option B: embedded metadata first (EPUB OPF package / PDF Info dict).
            // Publisher-authored data is dramatically more accurate than whatever
            // the file happened to be named when it landed on disk.
            var embedded = await _bookMetadataExtractor.ExtractAsync(filePath, cancellationToken);

            // Option A: filename parser — used as fallback when embedded metadata
            // is absent, and also to fill gaps (e.g. EPUB has title but no year).
            var parsed = FileNameParser.ParseBook(filePath);

            string? title = embedded?.Title;
            if (string.IsNullOrWhiteSpace(title)) title = parsed.Title;
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(filePath);

            string? author = embedded?.Author;
            if (string.IsNullOrWhiteSpace(author)) author = parsed.Author;

            int? year = embedded?.Year ?? parsed.Year;

            var isNew = existing == null;
            var book = existing ?? new MediaItem { LibraryId = library.Id };

            book.Title = title!;
            book.SortTitle = MediaStringHelpers.GetSortTitle(title!);
            book.Path = filePath;
            book.Type = MediaType.Book;
            book.Size = file.Size;
            book.DateModified = file.LastWriteUtc;

            if (!string.IsNullOrEmpty(author) && string.IsNullOrEmpty(book.Director))
            {
                book.Director = author;
            }

            // BookScanner stamps year / publisher / overview only when they come from
            // the publisher-embedded source, not the filename — filename-derived years
            // can be editions, reprints, or typos, and we'd rather let the metadata
            // provider supply them later if embedded data is missing.
            if (year.HasValue && !book.Year.HasValue)
            {
                book.Year = year;
            }
            if (embedded != null)
            {
                if (!string.IsNullOrWhiteSpace(embedded.Publisher) && string.IsNullOrEmpty(book.Studio))
                    book.Studio = embedded.Publisher;
                if (!string.IsNullOrWhiteSpace(embedded.Description) && string.IsNullOrEmpty(book.Overview))
                    book.Overview = embedded.Description;
            }

            if (isNew)
            {
                if (book.Id == Guid.Empty) book.Id = Guid.NewGuid();
                context.MediaItems.Add(book);

                var refreshMode = MetadataRefreshMode.Full;
                await _mediaAnalysisService.AnalyzeAsync(book, filePath, refreshMode, cancellationToken);

                _logger.LogDebug("[BookScanner] Added book: {Title}", title);
                return new ScanOperationResult(ScanResult.New, book.Id, EnqueueMetadata: true);
            }
            else
            {
                var needsEnrichment = MetadataEnrichmentPolicy.NeedsEnrichment(existing!, _strictEnrichment);
                if (needsEnrichment)
                {
                    _logger.LogDebug("[BookScanner] Queued metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, book.Id, EnqueueMetadata: true);
                }
                return new ScanOperationResult(ScanResult.Skipped, book.Id, EnqueueMetadata: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BookScanner] Error processing book file: {Path}", filePath);
            return new ScanOperationResult(ScanResult.Skipped, Guid.Empty, false);
        }
    }

    // ─────────────────────────────────────────────────────────────── Comic pipeline

    private async Task<ScanOperationResult> ProcessComicFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            var parsed = FileNameParser.ParseComic(filePath);
            var seriesName = string.IsNullOrWhiteSpace(parsed.SeriesName)
                ? Path.GetFileNameWithoutExtension(filePath)
                : parsed.SeriesName;

            var series = await EnsureComicSeriesAsync(context, seriesName, parsed.Year, library, filePath, cancellationToken);

            var isNew = existing == null;
            var issue = existing ?? new MediaItem { LibraryId = library.Id };

            var issueNumber = parsed.IssueNumber;
            var issueTitle = issueNumber.HasValue
                ? $"Issue #{issueNumber.Value}"
                : seriesName; // one-shot: use the series name as title

            // Preserve custom titles on re-scan (e.g. user-edited or metadata-provided names).
            if (!isNew && !string.IsNullOrEmpty(issue.Title) && issue.Title != issueTitle
                && !issue.Title.StartsWith("Issue #", StringComparison.OrdinalIgnoreCase))
            {
                issueTitle = issue.Title;
            }

            issue.Title = issueTitle;
            issue.SortTitle = issueNumber.HasValue ? $"Issue {issueNumber.Value:D4}" : MediaStringHelpers.GetSortTitle(issueTitle);
            issue.Path = filePath;
            issue.Type = MediaType.ComicIssue;
            issue.SeriesId = series.Id;
            issue.EpisodeNumber = issueNumber;     // reusing the column for issue number
            issue.Year = parsed.Year ?? issue.Year;
            issue.Size = file.Size;
            issue.DateModified = file.LastWriteUtc;

            if (isNew)
            {
                if (issue.Id == Guid.Empty) issue.Id = Guid.NewGuid();
                context.MediaItems.Add(issue);

                await _mediaAnalysisService.AnalyzeAsync(issue, filePath, MetadataRefreshMode.Full, cancellationToken);

                _logger.LogDebug("[BookScanner] Added comic issue: {Series} #{Issue}", seriesName, issueNumber);
                // Route this issue through the comic provider chain (ComicInfo primary, Wikidata fallback).
                return new ScanOperationResult(ScanResult.New, issue.Id, EnqueueMetadata: true);
            }
            else
            {
                var needsEnrichment = MetadataEnrichmentPolicy.NeedsEnrichment(existing!, _strictEnrichment);
                return new ScanOperationResult(ScanResult.Updated, issue.Id, EnqueueMetadata: needsEnrichment);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BookScanner] Error processing comic file: {Path}", filePath);
            return new ScanOperationResult(ScanResult.Skipped, Guid.Empty, false);
        }
    }

    /// <summary>
    /// Find or create a ComicSeries parent. Thread-safe via cache + striped locks.
    /// </summary>
    private async Task<MediaItem> EnsureComicSeriesAsync(
        AppDbContext context,
        string seriesName,
        int? year,
        Library library,
        string issuePath,
        CancellationToken cancellationToken)
    {
        var key = (library.Id, seriesName);
        if (_seriesCache.TryGetValue(key, out var cached))
            return cached;

        using (await LockParentAsync($"comicseries::{library.Id}::{seriesName}", cancellationToken))
        {
            if (_seriesCache.TryGetValue(key, out cached))
                return cached;

            // Double-check against DB in case another scan created it while we were waiting.
            var dbSeries = await context.MediaItems
                .FirstOrDefaultAsync(
                    m => m.LibraryId == library.Id
                         && m.Type == MediaType.ComicSeries
                         && m.Title == seriesName,
                    cancellationToken);

            if (dbSeries != null)
            {
                _seriesCache.TryAdd(key, dbSeries);
                return dbSeries;
            }

            var series = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Title = seriesName,
                SortTitle = MediaStringHelpers.GetSortTitle(seriesName),
                // Point the series at the folder containing the first issue we saw.
                Path = Path.GetDirectoryName(issuePath) ?? issuePath,
                Type = MediaType.ComicSeries,
                Year = year,
                DateModified = DateTime.UtcNow
            };

            context.MediaItems.Add(series);

            // SR-WI-035 — parent-creation saves run inside the parallel directory walk,
            // so they must take the scanner-wide write lock like the base class's
            // end-of-directory saves (SQLite tolerates a single writer).
            await _dbWriteLock.WaitAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _dbWriteLock.Release();
            }

            _seriesCache.TryAdd(key, series);
            // Mark for deferred metadata enrichment — enqueued after base.ScanLibraryAsync completes
            // so the provider sees all child issues in the DB before resolving series-level fields.
            _seriesNeedingEnrichment.TryAdd(series.Id, 0);
            _logger.LogInformation("[BookScanner] Created comic series: {Series}", seriesName);
            return series;
        }
    }

    /// <summary>
    /// Cleanup empty ComicSeries parents (no remaining issues).
    /// </summary>
    protected override async Task CleanupEmptyContainersAsync(
        AppDbContext context,
        Library library,
        CancellationToken cancellationToken)
    {
        var emptySeries = await context.MediaItems
            .Where(m => m.LibraryId == library.Id && m.Type == MediaType.ComicSeries)
            .Where(series => !context.MediaItems.Any(issue =>
                issue.Type == MediaType.ComicIssue && issue.SeriesId == series.Id))
            .ToListAsync(cancellationToken);

        if (emptySeries.Count > 0)
        {
            _logger.LogInformation("[BookScanner] Removing {Count} empty comic series", emptySeries.Count);
            context.MediaItems.RemoveRange(emptySeries);
        }
    }

    private sealed class SeriesCacheKeyComparer : IEqualityComparer<(Guid LibraryId, string Series)>
    {
        public bool Equals((Guid LibraryId, string Series) x, (Guid LibraryId, string Series) y)
            => x.LibraryId == y.LibraryId
               && string.Equals(x.Series, y.Series, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid LibraryId, string Series) obj)
            => HashCode.Combine(obj.LibraryId, obj.Series?.ToLowerInvariant());
    }
}
