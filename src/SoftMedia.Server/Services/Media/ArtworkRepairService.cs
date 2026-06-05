using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Finds artwork that went blank after a database-only restore (DB rows point at
/// <c>/cache/...</c> files the backup didn't include) and re-queues the owning items
/// for metadata enrichment so the art is re-fetched from providers.
///
/// Mapping rules: episode stills and season posters are only re-fetched when the parent
/// SERIES is enriched (the enricher propagates child art), so those map to the series.
/// Comic-issue covers are extracted from the archive file, not from a provider, so they
/// can only be recovered by a library re-scan — they are counted, not re-queued.
/// Locked items are honoured (skipped) here and again downstream in the metadata queue.
/// </summary>
public class ArtworkRepairService : IArtworkRepairService
{
    // Serializes repair runs across instances (scoped service): the auto-on-restore
    // worker and the admin button can't usefully scan the DB at the same time.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly AppDbContext _db;
    private readonly IMetadataQueue _metadataQueue;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ArtworkRepairService> _logger;

    public ArtworkRepairService(
        AppDbContext db,
        IMetadataQueue metadataQueue,
        IWebHostEnvironment env,
        ILogger<ArtworkRepairService> logger)
    {
        _db = db;
        _metadataQueue = metadataQueue;
        _env = env;
        _logger = logger;
    }

    public async Task<ArtworkRepairResult> RepairAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<ArtworkRepairResult> RunAsync(CancellationToken ct)
    {
        var webRoot = _env.WebRootPath
            ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

        // Distinct top-level items to re-enrich, and whether any was a comic issue
        // (recoverable only by re-scan, not a metadata refetch).
        var enrichTargets = new HashSet<Guid>();
        var missingImages = 0;
        var needsRescan = 0;
        var orphaned = 0;
        var scanned = 0;

        // 1. Media items with any LOCAL image reference (not an external http URL).
        //    We can't filter to a single prefix: most art is stored as /cache/... but
        //    embedded music covers are stored as an absolute filesystem path. Both go
        //    blank after a DB-only restore, so we detect by file existence below.
        var mediaRows = await _db.MediaItems
            .Where(m =>
                (m.PosterUrl != null && !m.PosterUrl.StartsWith("http")) ||
                (m.BackdropUrl != null && !m.BackdropUrl.StartsWith("http")) ||
                (m.CoverArtPath != null && !m.CoverArtPath.StartsWith("http")))
            .Select(m => new { m.Id, m.Type, m.SeriesId, m.PosterUrl, m.BackdropUrl, m.CoverArtPath })
            .ToListAsync(ct);

        foreach (var row in mediaRows)
        {
            scanned++;
            var anyMissing = false;
            foreach (var value in new[] { row.PosterUrl, row.BackdropUrl, row.CoverArtPath })
            {
                if (IsMissingLocalImage(value, webRoot))
                {
                    missingImages++;
                    anyMissing = true;
                }
            }
            if (!anyMissing) continue;

            // Comic-issue covers come from the archive, not a provider — re-scan only.
            if (row.Type == MediaType.ComicIssue)
            {
                needsRescan++;
                continue;
            }

            // Episode stills / season posters are re-fetched via the parent series.
            var targetId = row.Type is MediaType.Episode or MediaType.Season
                ? row.SeriesId
                : row.Id;

            if (targetId.HasValue)
            {
                enrichTargets.Add(targetId.Value);
            }
            else
            {
                // Orphaned child (episode/season with no parent series) — a data-integrity
                // issue, not a normal re-scan case. Counted toward NeedsRescan but tracked
                // separately so it can be flagged in the log.
                needsRescan++;
                orphaned++;
            }
        }

        // 2. Cast headshots (Person.ImagePath). Cast art is re-fetched when the media
        //    items that reference the person are enriched, so map persons -> their media.
        var missingPersonIds = new List<int>();
        var personRows = await _db.Persons
            .Where(p => p.ImagePath != null && !p.ImagePath.StartsWith("http"))
            .Select(p => new { p.Id, p.ImagePath })
            .ToListAsync(ct);

        foreach (var p in personRows)
        {
            scanned++;
            if (IsMissingLocalImage(p.ImagePath, webRoot))
            {
                missingImages++;
                missingPersonIds.Add(p.Id);
            }
        }

        if (missingPersonIds.Count > 0)
        {
            var mediaForPersons = await _db.MediaItemCasts
                .Where(c => missingPersonIds.Contains(c.PersonId))
                .Select(c => c.MediaItemId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in mediaForPersons) enrichTargets.Add(id);
        }

        if (orphaned > 0)
        {
            _logger.LogWarning(
                "Artwork repair: {Count} orphaned episode/season item(s) have missing art but no parent series — a re-scan is needed to rebuild the relationship.",
                orphaned);
        }

        if (enrichTargets.Count == 0)
        {
            _logger.LogInformation(
                "Artwork repair: scanned {Scanned} local references, {Missing} missing, nothing to re-fetch ({Rescan} need a re-scan).",
                scanned, missingImages, needsRescan);
            return new ArtworkRepairResult(scanned, missingImages, 0, 0, needsRescan);
        }

        // 3. Resolve each target's library type (drives provider routing) and lock state,
        //    then re-queue. Locked items are skipped to honour the Fix Match lock.
        var targets = await _db.MediaItems
            .Where(m => enrichTargets.Contains(m.Id))
            .Join(_db.Libraries, m => m.LibraryId, l => l.Id,
                (m, l) => new { m.Id, m.MetadataLocked, LibType = l.Type })
            .ToListAsync(ct);

        var reEnqueued = 0;
        var lockedSkipped = 0;
        var failedEnqueue = 0;

        foreach (var t in targets)
        {
            if (t.MetadataLocked)
            {
                lockedSkipped++;
                continue;
            }
            try
            {
                await _metadataQueue.EnqueueMetadataRefreshAsync(t.Id, t.LibType, refreshImages: true);
                reEnqueued++;
            }
            catch (Exception ex)
            {
                failedEnqueue++;
                _logger.LogWarning(ex, "Artwork repair: failed to enqueue {Id} for re-enrichment", t.Id);
            }
        }

        if (failedEnqueue > 0)
        {
            _logger.LogError(
                "Artwork repair finished with failures: scanned {Scanned}, missing {Missing}, re-queued {ReEnqueued}, locked-skipped {Locked}, need-rescan {Rescan}, failed-enqueue {Failed}.",
                scanned, missingImages, reEnqueued, lockedSkipped, needsRescan, failedEnqueue);
        }
        else
        {
            _logger.LogInformation(
                "Artwork repair complete: scanned {Scanned}, missing {Missing}, re-queued {ReEnqueued}, locked-skipped {Locked}, need-rescan {Rescan}.",
                scanned, missingImages, reEnqueued, lockedSkipped, needsRescan);
        }

        return new ArtworkRepairResult(scanned, missingImages, reEnqueued, lockedSkipped, needsRescan, failedEnqueue);
    }

    /// <summary>
    /// True when <paramref name="value"/> is a local image reference whose backing file
    /// is absent. Handles both web-relative <c>/cache/...</c> paths (resolved under the
    /// web root) and absolute filesystem paths (e.g. embedded music covers written by the
    /// scanner). External <c>http(s)</c> URLs are left alone — they self-heal via the
    /// image proxy. Never throws: a malformed path is treated as "not missing" so one bad
    /// row can't abort the whole sweep.
    /// </summary>
    private bool IsMissingLocalImage(string? value, string webRoot)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            string fullPath;
            if (value.StartsWith('/'))
                fullPath = Path.Combine(webRoot, value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            else if (Path.IsPathRooted(value))
                fullPath = value; // absolute filesystem path (e.g. extracted embedded cover)
            else
                fullPath = Path.Combine(webRoot, value.Replace('/', Path.DirectorySeparatorChar));

            return !File.Exists(fullPath);
        }
        catch (Exception ex)
        {
            // PathTooLongException / ArgumentException on a malformed stored path: don't
            // flag it (can't repair what we can't resolve) and don't abort the sweep.
            _logger.LogWarning(ex, "Artwork repair: could not check image path '{Value}'", value);
            return false;
        }
    }
}
