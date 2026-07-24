using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;

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
            "Daily: deletes cached artwork for items that no longer exist in the database. "
            + "Missing (offline) items keep their artwork until they are hard-deleted.",
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

    /// <summary>
    /// One sweep: valid ids from the RAW MediaItems DbSet (row-existence — IsMissing rows
    /// included, their art is retained), then delete cached files with no matching row.
    /// Public so tests can drive it without the timer. Returns the deleted-file count.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imageCache = scope.ServiceProvider.GetRequiredService<IImageCacheService>();

        var validIds = new HashSet<Guid>(await db.MediaItems.Select(m => m.Id).ToListAsync(ct));
        var deleted = imageCache.CleanupOrphanedImages(validIds);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Image cache cleanup removed {Count} orphaned file(s) ({Rows} live DB rows).",
                deleted, validIds.Count);
        }
        return deleted;
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
