using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media.Detection;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// CM-WI-003: one-shot boot sweep that applies <see cref="ChapterMarkerMapper"/> to items
/// whose chapters were stored by earlier scans, so chapter-derived intro/credits markers
/// don't wait for the next full rescan. Idempotent by construction — a re-run computes the
/// same values and writes nothing — so it simply runs every boot (self-healing, same
/// philosophy as the trickplay sweep) and logs only when it changed something.
///
/// Precedence note: Chapter beats Detected (the file's own authoring is ground truth), so
/// this sweep MAY overwrite detection-written values. It never touches items without
/// stored chapters. It DOES clear a Chapter-sourced value whose stored chapters no longer
/// map (e.g. a mapper rule tightened, as the CM-WI-004 span caps did) — stored chapters
/// are the file's last-probed truth, and a marker our own rules now reject must not
/// survive as a stale skip target. Detected values are never cleared; the next detection
/// run re-fills cleared segments from cached fingerprints.
/// </summary>
public class ChapterMarkerBackfillService : BackgroundService
{
    // Let the DB/migrations settle before touching MediaItems.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);

    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChapterMarkerBackfillService> _logger;

    public ChapterMarkerBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<ChapterMarkerBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fully guarded: a fault here must never tear down the host (BackgroundService
        // failures stop the application under the .NET default behaviour).
        try
        {
            await Task.Delay(SettleDelay, stoppingToken);
            var (checked_, updated) = await RunOnceAsync(stoppingToken);
            if (updated > 0)
            {
                _logger.LogInformation(
                    "Chapter-marker backfill: updated {Updated} of {Checked} chaptered item(s).", updated, checked_);
            }
            else
            {
                _logger.LogDebug("Chapter-marker backfill: {Checked} chaptered item(s) already current.", checked_);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown before the sweep finished — next boot repeats it harmlessly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chapter-marker backfill failed; will retry on next start.");
        }
    }

    /// <summary>
    /// Core sweep, separated from the hosted-service plumbing so tests can drive it
    /// directly (project convention; no InternalsVisibleTo).
    /// </summary>
    public async Task<(int Checked, int Updated)> RunOnceAsync(CancellationToken ct)
    {
        int checkedCount = 0, updatedCount = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Only video items that actually have stored chapters are candidates.
        var candidateIds = await db.MediaItems
            .Where(m => (m.Type == MediaType.Movie || m.Type == MediaType.Episode)
                && m.Chapters.Any())
            .Select(m => m.Id)
            .ToListAsync(ct);

        foreach (var chunk in candidateIds.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var items = await db.MediaItems
                .Include(m => m.Chapters)
                .Where(m => chunk.Contains(m.Id))
                .ToListAsync(ct);

            foreach (var item in items)
            {
                checkedCount++;
                var chapters = item.Chapters
                    .OrderBy(c => c.StartTime)
                    .Select(c => (c.StartTime, c.Title))
                    .ToList();

                var markers = ChapterMarkerMapper.Map(chapters, item.Duration);
                if (ApplyMarkers(item, markers)) updatedCount++;
            }

            await db.SaveChangesAsync(ct);
        }

        return (checkedCount, updatedCount);
    }

    /// <summary>
    /// Writes chapter-derived spans onto the item (and clears Chapter-sourced values the
    /// mapper no longer produces); returns true when anything changed. Same invariant as
    /// the scan path: Chapter-sourced columns mirror the (last-probed) chapters.
    /// </summary>
    private static bool ApplyMarkers(MediaItem item, ChapterMarkerResult markers)
    {
        var changed = false;

        if (markers.Intro is { } intro)
        {
            if (item.IntroStart != intro.Start || item.IntroEnd != intro.End || item.IntroSource != DetectionSource.Chapter)
            {
                item.IntroStart = intro.Start;
                item.IntroEnd = intro.End;
                item.IntroSource = DetectionSource.Chapter;
                changed = true;
            }
        }
        else if (item.IntroSource == DetectionSource.Chapter)
        {
            item.IntroStart = null;
            item.IntroEnd = null;
            item.IntroSource = null;
            changed = true;
        }

        if (markers.Credits is { } credits)
        {
            if (item.CreditsStart != credits.Start || item.CreditsEnd != credits.End || item.CreditsSource != DetectionSource.Chapter)
            {
                item.CreditsStart = credits.Start;
                item.CreditsEnd = credits.End;
                item.CreditsSource = DetectionSource.Chapter;
                changed = true;
            }
        }
        else if (item.CreditsSource == DetectionSource.Chapter)
        {
            item.CreditsStart = null;
            item.CreditsEnd = null;
            item.CreditsSource = null;
            changed = true;
        }

        return changed;
    }
}
