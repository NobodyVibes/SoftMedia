using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// SR-WI-036 — weekly "retry amnesty" for metadata fetches. When a provider outage outlasts
/// the retry ladder (1m/5m/30m/4h), items are flagged <c>IsRetryExhausted</c> and nothing ever
/// retried them again. This task clears the flag on every non-locked item once a week and
/// re-enqueues those that still need enrichment, so transient outages self-heal without admin
/// intervention. Locked items (<c>MetadataLocked</c>) are never touched — an explicit admin
/// match must not be re-fetched. Missing items (<c>IsMissing</c>) get their flag cleared but
/// are not enqueued (no point spending provider quota on hidden items; a heal-on-reappear scan
/// re-enqueues them through the normal path).
///
/// Modeled on <see cref="ScheduledScanService"/>: fixed weekly cadence anchored on the task's
/// own LastRunUtc in the scheduled-task registry (persisted across reboots), admin-triggerable
/// from the Background Tasks page (P1-WI-005 style). A separate task rather than a rider on
/// the Metadata Refresh job because that job's cadence is admin-configurable in days (default
/// 30, 0 = disabled) — hitching amnesty to it would break the weekly contract and silently
/// disable self-healing whenever the refresh schedule is off.
/// </summary>
public class MetadataRetryAmnestyService : BackgroundService, IManuallyTriggerableTask
{
    public static readonly TimeSpan AmnestyInterval = TimeSpan.FromDays(7);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultCheckPeriod = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetadataRetryAmnestyService> _logger;
    private readonly IScheduledTaskRegistry _registry;
    private readonly TimeSpan _checkPeriod;

    public MetadataRetryAmnestyService(
        IServiceScopeFactory scopeFactory,
        ILogger<MetadataRetryAmnestyService> logger,
        IScheduledTaskRegistry registry,
        TimeSpan? checkPeriod = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;
        _checkPeriod = checkPeriod ?? DefaultCheckPeriod;
    }

    public string TaskName => ScheduledTaskNames.MetadataRetryAmnesty;

    /// <summary>
    /// Admin "Run now": kick one amnesty pass in the background (the trigger endpoint returns
    /// 202; the outcome lands on the Background Tasks page via the registry report).
    /// </summary>
    public void TriggerNow()
    {
        _ = Task.Run(async () =>
        {
            try { await RunAmnestyAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Manually triggered retry amnesty failed."); }
        });
    }

    /// <summary>
    /// The scheduled-run decision, kept pure for tests (mirrors
    /// <see cref="ScheduledScanService.IsDue"/>): due when the task never ran, when the last
    /// attempt failed (retried at the check period, not deferred a full week), or when a full
    /// interval has elapsed since the last run.
    /// </summary>
    public static bool IsDue(DateTime? lastRunUtc, string? lastResult, DateTime nowUtc)
    {
        if (lastRunUtc == null) return true;
        if (lastResult == "Failed") return true;
        return nowUtc >= lastRunUtc.Value.Add(AmnestyInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MetadataRetryAmnestyService started. Check period {Period}.", _checkPeriod);

        // Let startup settle (and TaskStatusPersistenceService restore LastRunUtc) first.
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = _registry.GetAll().FirstOrDefault(t => t.Name == TaskName);
                if (IsDue(status?.LastRunUtc, status?.LastResult, DateTime.UtcNow))
                {
                    // RunAmnestyAsync reports to the registry, stamping LastRunUtc. Pace the
                    // next check (success recomputes NextRun below; failure retries then too).
                    await RunAmnestyAsync(stoppingToken);
                    _registry.SetNextRun(TaskName, DateTime.UtcNow.Add(_checkPeriod));
                    await Task.Delay(_checkPeriod, stoppingToken);
                    continue;
                }

                var nextDue = status!.LastRunUtc!.Value.Add(AmnestyInterval);
                _registry.SetNextRun(TaskName, nextDue);

                var wait = nextDue - DateTime.UtcNow;
                if (wait > _checkPeriod) wait = _checkPeriod;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MetadataRetryAmnestyService loop iteration failed.");
                try { await Task.Delay(_checkPeriod, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("MetadataRetryAmnestyService stopped.");
    }

    /// <summary>
    /// One amnesty pass: clear <c>IsRetryExhausted</c> (and stale retry rows) on every
    /// non-locked item, then re-enqueue the non-missing ones that still need enrichment
    /// through the central metadata queue (which owns provider rate limiting and re-checks
    /// the lock at processing time). Public so tests and TriggerNow drive one run directly.
    /// Returns the number of items re-enqueued, or -1 on failure.
    /// </summary>
    /// <summary>
    /// SM-WI-042 — decaying re-grant interval: the Nth amnesty for an item schedules the
    /// next one 14, then 28 days out (capped) instead of every flat week. The first
    /// grant needs no gate (NextAmnestyUtc is null right after exhaustion). Combined
    /// with the SM-WI-040 negative cache, a never-matching item costs a few cheap DB
    /// checks per month instead of full provider sweeps per week. Pure for tests.
    /// </summary>
    public static TimeSpan NextAmnestyDelay(int amnestyCount)
        => TimeSpan.FromDays(Math.Min(28, 7 * Math.Pow(2, amnestyCount + 1)));

    public async Task<int> RunAmnestyAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<IMetadataQueue>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            // SM-WI-042: items whose decayed next-amnesty time hasn't arrived stay
            // exhausted and untouched this pass.
            var now = DateTime.UtcNow;
            var exhausted = await db.MediaItems
                .Where(m => m.IsRetryExhausted && !m.MetadataLocked
                    && (m.NextAmnestyUtc == null || m.NextAmnestyUtc <= now))
                .ToListAsync(ct);

            if (exhausted.Count == 0)
            {
                _registry.Report(TaskName, "Success", sw.ElapsedMilliseconds);
                _logger.LogInformation("Retry amnesty: no exhausted items due.");
                return 0;
            }

            foreach (var item in exhausted)
            {
                item.IsRetryExhausted = false;
                // Missing items get their flag cleared but consume no quota (not
                // enqueued below) — don't advance their decay ladder either.
                if (!item.IsMissing)
                {
                    item.NextAmnestyUtc = now + NextAmnestyDelay(item.AmnestyCount);
                    item.AmnestyCount++;
                }
            }

            // Drop any stale retry bookkeeping so the cleared items restart the ladder at tier 1.
            var ids = exhausted.Select(m => m.Id).ToHashSet();
            var staleRetries = await db.MetadataRetries
                .Where(r => ids.Contains(r.MediaItemId))
                .ToListAsync(ct);
            db.MetadataRetries.RemoveRange(staleRetries);

            await db.SaveChangesAsync(ct);

            var enrichmentMode = await settings.GetSettingAsync("MetadataEnrichmentMode", "Relaxed");
            var strictMode = enrichmentMode == "Strict";

            var enqueued = 0;
            foreach (var item in exhausted)
            {
                if (item.IsMissing) continue;
                if (!MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode)) continue;

                await queue.EnqueueMetadataRefreshAsync(
                    item.Id, MediaTypeLibraryMap.ForMediaType(item.Type), refreshImages: true);
                enqueued++;
            }

            _registry.Report(TaskName, "Success", sw.ElapsedMilliseconds);
            _logger.LogInformation(
                "Retry amnesty: cleared {Cleared} exhausted item(s), re-enqueued {Enqueued}.",
                exhausted.Count, enqueued);
            return enqueued;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry amnesty pass failed.");
            _registry.Report(TaskName, "Failed", sw.ElapsedMilliseconds, ex.Message);
            return -1;
        }
    }
}
