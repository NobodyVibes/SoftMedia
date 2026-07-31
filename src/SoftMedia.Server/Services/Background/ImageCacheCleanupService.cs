using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// SR-WI-037 — daily sweep that deletes cached artwork whose media item no longer exists in
/// the database. Before this worker, <c>ImageCacheService.CleanupOrphanedImages</c> had zero
/// callers, so posters/covers for deleted or renamed items leaked forever.
///
/// Orphan criterion is ROW-EXISTENCE: the valid-id set is built from the RAW
/// <c>MediaItems</c> DbSet (no <c>.Visible()</c> / IsMissing filter — and AppDbContext has no
/// global query filters), so soft-deleted (<c>IsMissing</c>, SR-WI-011) items KEEP their
/// artwork and it heals when the drive returns. Only guids with no DB row at all are orphans.
///
/// The same sweep also runs the reverse repair: rows whose PosterUrl is still a provider URL
/// while the poster is already cached on disk get repointed at the cached file, so they stop
/// being re-fetched through /api/v1/image/proxy on every library view.
///
/// Registered with the scheduled-task registry (P1-WI-005) for admin-visible telemetry and
/// exposes <see cref="IManuallyTriggerableTask"/> so the Background Tasks page can run it on
/// demand (POST /api/v1/admin/tasks/{name}/trigger, R-WI-008 dispatch).
/// </summary>
public class ImageCacheCleanupService : BackgroundService, IManuallyTriggerableTask
{
    /// <summary>Registry/dispatch name. Not in <see cref="ScheduledTaskNames"/> (file owned by
    /// concurrent work); this task self-registers its descriptor instead — see ctor.</summary>
    public const string RegisteredTaskName = "Image Cache Cleanup";

    // Let startup (and the initial post-boot scans) settle before the first sweep.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Period = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageCacheCleanupService> _logger;
    private readonly IScheduledTaskRegistry _registry;

    public ImageCacheCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<ImageCacheCleanupService> logger,
        IScheduledTaskRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;

        // Self-register the descriptor rather than adding it to ScheduledTaskRegistrySeeder.
        // Register is add-or-keep, so a future seeder entry for the same name stays harmless.
        _registry.Register(
            RegisteredTaskName,
            "Daily: deletes cached artwork, trickplay sheets, thumbnails, cast headshots and "
            + "extracted subtitles for items that no longer exist in the database, expires old "
            + "image-proxy copies, and removes people/genre rows nothing references any more. "
            + "Missing (offline) items keep their artifacts until they are hard-deleted. Also "
            + "repoints items whose poster is already cached on disk but is still being fetched "
            + "through the image proxy.",
            TaskSchedule.Scheduled,
            supportsManualTrigger: true);

        // Program.cs restores persisted telemetry (TaskStatusStore.Load) BEFORE hosted services
        // are constructed, so a name registered here misses that pass. Re-apply our own row so
        // the Background Tasks card keeps last-run info across a reboot, like seeded tasks do.
        TryRestoreOwnPersistedTelemetry();
    }

    public string TaskName => RegisteredTaskName;

    /// <summary>
    /// Admin "Run now". Fire-and-forget per the IManuallyTriggerableTask contract (the
    /// endpoint returns 202); outcome lands in the registry. No nextRunUtc is stamped —
    /// the daily loop keeps its own cadence and its NextRun stays authoritative.
    /// </summary>
    public void TriggerNow()
        => _ = Task.Run(() => RunOnceReportedAsync(nextRunUtc: null, CancellationToken.None));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceReportedAsync(DateTime.UtcNow.Add(Period), stoppingToken);

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Age below which a thumbnail with an unknown key survives the sweep — the
    /// thumbnails directory mixes media-item keys with proxy-derived keys that can never
    /// appear in the DB, so unknown keys are only reaped once they have gone stale.</summary>
    private static readonly TimeSpan ThumbnailMinAge = TimeSpan.FromDays(7);

    /// <summary>Age after which an image-proxy copy (and its .404 sentinel) expires. Cache
    /// hits refresh the file's mtime, so only genuinely unused entries reach this age.</summary>
    private static readonly TimeSpan ProxyMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// One sweep: valid ids from the RAW MediaItems DbSet (row-existence — IsMissing rows
    /// included, their artifacts are retained), then delete cached files with no matching
    /// row across every derived-artifact store: artwork, trickplay sheets, thumbnails,
    /// cast headshots, extracted subtitles, and age-expired image-proxy copies.
    /// Public so tests can drive it without the timer. Returns the deleted-file count.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imageCache = scope.ServiceProvider.GetRequiredService<IImageCacheService>();
        var trickplay = scope.ServiceProvider.GetRequiredService<ITrickplayService>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<IThumbnailService>();
        var subtitles = scope.ServiceProvider.GetRequiredService<ISubtitleService>();
        var proxyStore = scope.ServiceProvider.GetRequiredService<IProxyImageStore>();

        var validIds = new HashSet<Guid>(await db.MediaItems.Select(m => m.Id).ToListAsync(ct));
        var deleted = imageCache.CleanupOrphanedImages(validIds);

        deleted += trickplay.CleanupOrphans(validIds);
        deleted += thumbnails.CleanupOrphans(validIds, ThumbnailMinAge);

        // Cast headshots are keyed by Person.ExternalId; valid = still referenced by any
        // cast row (Person rows themselves are global — shared across libraries).
        var validCastIds = new HashSet<int>(await db.MediaItemCasts
            .Where(c => c.Person!.ExternalId != null)
            .Select(c => c.Person!.ExternalId!.Value)
            .Distinct()
            .ToListAsync(ct));
        deleted += imageCache.CleanupOrphanedCastImages(validCastIds);

        // MC-WI-005: Person rows referenced by NO cast row are unreachable in the UI and
        // were previously never deleted anywhere — unbounded growth as libraries churn.
        // Set-based delete: SQLite serializes writers, so the NOT-EXISTS is evaluated
        // atomically with the delete; a scan that re-credits such a person afterwards
        // simply re-creates the row via the aggregator's ExternalId/Name dedup.
        // (ExecuteDelete does not run on the EF InMemory provider — sweep tests use SQLite.)
        var orphanPersons = await db.Persons
            .Where(p => !db.MediaItemCasts.Any(c => c.PersonId == p.Id))
            .ExecuteDeleteAsync(ct);
        if (orphanPersons > 0)
        {
            _logger.LogInformation("Removed {Count} Person row(s) no longer credited on any media item.", orphanPersons);
        }

        // MC-WI-006: same treatment for Genre rows with no MediaItemGenre link — invisible
        // in the UI, left behind by library deletion's cascade. (The deliberate,
        // admin-triggered GenreMaintenanceService normalisation pass is a different tool:
        // it rewrites taxonomy; this only reaps rows nothing references.)
        var orphanGenres = await db.Genres
            .Where(g => !db.MediaItemGenres.Any(l => l.GenreId == g.Id))
            .ExecuteDeleteAsync(ct);
        if (orphanGenres > 0)
        {
            _logger.LogInformation("Removed {Count} Genre row(s) no longer linked to any media item.", orphanGenres);
        }

        // Extracted-subtitle cache is keyed by source path hash; valid = any row's path.
        var validPaths = await db.MediaItems
            .Where(m => m.Path != null)
            .Select(m => m.Path!)
            .ToListAsync(ct);
        deleted += subtitles.CleanupOrphanedVtt(validPaths);

        deleted += proxyStore.SweepExpired(ProxyMaxAge);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cache cleanup removed {Count} orphaned file(s)/director(ies) ({Rows} live DB rows).",
                deleted, validIds.Count);
        }

        await AdoptCachedPostersAsync(db, imageCache, proxyStore, ct);
        return deleted;
    }

    /// <summary>
    /// Heals rows whose PosterUrl still points at the provider even though that poster is
    /// already cached on disk. The DTO layer proxies any http(s) poster through
    /// /api/v1/image/proxy, so such a row re-downloads its art into cache/images/proxy on
    /// every library view while the identical file sits in cache/images/{movies,tv,…}.
    /// The drift is otherwise permanent: the item looks "complete" to
    /// MetadataEnrichmentPolicy, so no rescan re-enqueues it and nothing rewrites the column.
    /// (The write path that produced it — an enrichment save clobbering the image queue's
    /// write-back — is fixed in MetadataAggregator; this repairs rows written before that.)
    /// </summary>
    private async Task<int> AdoptCachedPostersAsync(AppDbContext db, IImageCacheService imageCache, IProxyImageStore proxyStore, CancellationToken ct)
    {
        try
        {
            var cached = imageCache.GetCachedPosterPaths();
            if (cached.Count == 0) return 0;

            // Bounded by items whose provider art is NOT cached, so this stays small; local
            // sidecar art never matches (its PosterUrl is already a /cache path).
            var candidates = await db.MediaItems
                .Where(m => m.PosterUrl != null && m.PosterUrl.StartsWith("http"))
                .ToListAsync(ct);

            var healed = candidates.Where(m => cached.ContainsKey(m.Id)).ToList();
            if (healed.Count == 0) return 0;

            foreach (var item in healed)
            {
                // The provider URL being overwritten is the ONLY surviving key to the
                // proxy's hash-named copy of the same image — delete it now or it becomes
                // permanently unattributable (it only ages out via the TTL sweep).
                proxyStore.DeleteCachedCopy(item.PosterUrl!);
                item.PosterUrl = cached[item.Id];
            }

            // Same invalidation the image queue does after a write-back: the home rows and
            // hero are materialised DTO caches and would keep serving the proxy URL.
            var libraryIds = healed.Select(m => m.LibraryId).Distinct().ToList();
            var staleRecents = await db.LibraryRecentCaches
                .Where(c => libraryIds.Contains(c.LibraryId))
                .ToListAsync(ct);
            db.LibraryRecentCaches.RemoveRange(staleRecents);

            var heroCache = await db.HeroCaches.FirstOrDefaultAsync(c => c.Id == 1, ct);
            if (heroCache != null) db.HeroCaches.Remove(heroCache);

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Image cache cleanup pointed {Count} item(s) at artwork already cached on disk "
                + "(they were being re-fetched through the image proxy on every view).",
                healed.Count);
            return healed.Count;
        }
        catch (Exception ex)
        {
            // Never fail the sweep over the repair pass — orphan deletion above already ran.
            _logger.LogWarning(ex, "Failed to adopt cached posters during image cache cleanup");
            return 0;
        }
    }

    private async Task RunOnceReportedAsync(DateTime? nextRunUtc, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await RunOnceAsync(ct);
            _registry.Report(RegisteredTaskName, "Success", sw.ElapsedMilliseconds, nextRunUtc: nextRunUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure — leave the last real result on the dashboard.
        }
        catch (Exception ex)
        {
            // Never let an exception kill the background loop (or the fire-and-forget trigger).
            _logger.LogError(ex, "Image cache cleanup run failed.");
            _registry.Report(RegisteredTaskName, "Failed", sw.ElapsedMilliseconds, ex.Message, nextRunUtc);
        }
    }

    /// <summary>Best-effort restore of THIS task's persisted telemetry row (see ctor note).
    /// Only our just-registered row is touched, so nothing another task reported is clobbered.</summary>
    private void TryRestoreOwnPersistedTelemetry()
    {
        try
        {
            var path = TaskStatusStore.DefaultPath();
            if (!File.Exists(path)) return;
            var rows = JsonSerializer.Deserialize<List<PersistedTaskStatus>>(File.ReadAllText(path));
            var mine = rows?.FirstOrDefault(r => r.Name == RegisteredTaskName && r.LastRunUtc != null);
            if (mine != null)
            {
                _registry.LoadPersisted(mine.Name, mine.LastRunUtc, mine.LastResult,
                    mine.LastRunDurationMs, mine.LastError, mine.NextRunUtc);
            }
        }
        catch
        {
            // Telemetry restore is cosmetic; a corrupt file must never block startup.
        }
    }
}
