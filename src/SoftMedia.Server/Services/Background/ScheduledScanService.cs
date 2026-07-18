using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Scanning;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// R-WI-008 — periodic full-library scans as a backstop for changes the realtime file watcher
/// can miss (network shares, removable drives, missed FS events). Driven by the
/// <c>LibraryScanIntervalHours</c> setting (0 = disabled, the default). Enqueues a scan for
/// EVERY library via <see cref="ILibraryScanQueueService"/>, which already dedups per-library
/// queued/running jobs and serialises execution — so overlapping schedules coalesce for free.
///
/// Scheduling anchor: the task's own <c>LastRunUtc</c> in the scheduled-task registry, which
/// <see cref="TaskStatusPersistenceService"/> persists across reboots — so a nightly interval
/// keeps its cadence through restarts, and an overdue scan (or a first-time enable, where
/// LastRunUtc is null) fires promptly. The interval setting is re-read every loop iteration
/// (bounded by <see cref="_checkPeriod"/>), so changes take effect within minutes, no restart.
/// </summary>
public class ScheduledScanService : BackgroundService, IManuallyTriggerableTask
{
    public const string IntervalSettingKey = "LibraryScanIntervalHours";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultCheckPeriod = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledScanService> _logger;
    private readonly IScheduledTaskRegistry _registry;
    private readonly TimeSpan _checkPeriod;

    // Completed by TriggerNow() to wake the scheduler loop so it recomputes NextRun right away.
    // volatile: TriggerNow runs on HTTP threads; without it a stale reference could eat the wake.
    // A wake can still be lost in the tiny consumed-but-not-yet-re-armed window, which is benign:
    // the scans were already enqueued synchronously, so the only consequence is the dashboard's
    // NextRun staying stale until the next check-period tick (≤5 min).
    private volatile TaskCompletionSource<bool> _manualTrigger =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScheduledScanService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledScanService> logger,
        IScheduledTaskRegistry registry,
        TimeSpan? checkPeriod = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;
        _checkPeriod = checkPeriod ?? DefaultCheckPeriod;
    }

    public string TaskName => ScheduledTaskNames.ScheduledLibraryScan;

    /// <summary>
    /// Admin "Run now": enqueue a scan of every library immediately (even while the schedule is
    /// disabled — an explicit request wins), then wake the scheduler so NextRun is recomputed.
    /// </summary>
    public void TriggerNow()
    {
        EnqueueAllLibraries();
        _manualTrigger.TrySetResult(true);
    }

    /// <summary>
    /// The scheduled-run decision, kept pure for tests. Due when the interval is enabled AND
    /// (the task never ran, i.e. first enable → run promptly, OR the last attempt FAILED —
    /// a failure must be retried at the check period, not silently deferred a whole interval,
    /// since Report stamps LastRunUtc for failures too — OR one full interval has elapsed
    /// since the last run, which persistence carries across reboots; both anchors survive them).
    /// The retry pacing for the Failed case lives in the loop (it waits a check period after a
    /// failed run), so this returning true immediately does not tight-loop.
    /// </summary>
    public static bool IsDue(DateTime? lastRunUtc, string? lastResult, int intervalHours, DateTime nowUtc)
    {
        if (intervalHours <= 0) return false;
        if (lastRunUtc == null) return true;
        if (lastResult == "Failed") return true;
        return nowUtc >= lastRunUtc.Value.AddHours(intervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledScanService started. Check period {Period}.", _checkPeriod);

        // Let startup settle (and TaskStatusStore restore LastRunUtc) before the first decision.
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var intervalHours = await GetIntervalHoursAsync();
                if (intervalHours <= 0)
                {
                    // Disabled: honest dashboard (no NextRun), re-check config each period.
                    _registry.SetNextRun(TaskName, null);
                    await WaitForTriggerOrDelayAsync(_checkPeriod, stoppingToken);
                    continue;
                }

                var status = GetOwnStatus();
                if (IsDue(status?.LastRunUtc, status?.LastResult, intervalHours, DateTime.UtcNow))
                {
                    // EnqueueAllLibraries reports to the registry, stamping LastRunUtc — the next
                    // loop iteration recomputes NextRun from it. On failure, pace the retry to the
                    // check period (IsDue stays true for a Failed result, so without this wait a
                    // persistent failure would retry in a tight loop).
                    if (EnqueueAllLibraries() < 0)
                    {
                        _registry.SetNextRun(TaskName, DateTime.UtcNow.Add(_checkPeriod));
                        await WaitForTriggerOrDelayAsync(_checkPeriod, stoppingToken);
                    }
                    continue;
                }

                var nextDue = status!.LastRunUtc!.Value.AddHours(intervalHours);
                _registry.SetNextRun(TaskName, nextDue);

                // Sleep until due, but never longer than the check period, so interval changes
                // (and manual triggers) are honoured within minutes.
                var wait = nextDue - DateTime.UtcNow;
                if (wait > _checkPeriod) wait = _checkPeriod;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                await WaitForTriggerOrDelayAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScheduledScanService loop iteration failed.");
                try { await Task.Delay(_checkPeriod, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("ScheduledScanService stopped.");
    }

    /// <summary>
    /// Enqueue a library-scan job for every library and report the outcome to the registry.
    /// Never throws for routine failures — the tasks page shows Failed instead (the trigger
    /// endpoint has already returned 202 by the time a failure could surface anywhere else),
    /// and the scheduler retries a Failed run at the next check period rather than waiting a
    /// full interval (see <see cref="IsDue"/>). Per-library failures don't abort the batch:
    /// the remaining libraries are still enqueued, and the run reports Failed so it retries
    /// (the queue's dedup makes the retry a no-op for the libraries that did enqueue).
    /// Returns the number of libraries enqueued, or -1 on any failure.
    /// Public so tests can drive one run directly (matches RefreshTokenCleanupService.PruneExpiredAsync).
    /// </summary>
    public int EnqueueAllLibraries()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<ILibraryScanQueueService>();

            var libraries = db.Libraries.Select(l => new { l.Id, l.Name }).ToList();
            var enqueued = 0;
            string? firstError = null;
            foreach (var lib in libraries)
            {
                try
                {
                    // Dedup lives in the queue: an already queued/running scan is returned as-is.
                    queue.EnqueueScan(lib.Id, lib.Name);
                    enqueued++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled scan failed to enqueue library {Name}.", lib.Name);
                    firstError ??= $"{lib.Name}: {ex.Message}";
                }
            }

            if (firstError != null)
            {
                var failedCount = libraries.Count - enqueued;
                _registry.Report(TaskName, "Failed", sw.ElapsedMilliseconds,
                    $"{failedCount} of {libraries.Count} libraries failed to enqueue. First error — {firstError}");
                return -1;
            }

            _registry.Report(TaskName, "Success", sw.ElapsedMilliseconds);
            _logger.LogInformation("Scheduled scan enqueued {Count} library scan job(s).", enqueued);
            return enqueued;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled scan failed to enqueue library scans.");
            _registry.Report(TaskName, "Failed", sw.ElapsedMilliseconds, ex.Message);
            return -1;
        }
    }

    private ScheduledTaskStatus? GetOwnStatus()
        => _registry.GetAll().FirstOrDefault(t => t.Name == TaskName);

    private async Task<int> GetIntervalHoursAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var setting = await settings.GetSettingAsync(IntervalSettingKey);
        return int.TryParse(setting?.Value, out var hours) ? hours : 0; // unparsable/absent = off
    }

    private async Task WaitForTriggerOrDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        var trigger = _manualTrigger.Task;
        await Task.WhenAny(trigger, Task.Delay(delay, ct));
        if (trigger.IsCompleted)
        {
            // Re-arm for the next manual trigger.
            _manualTrigger = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        ct.ThrowIfCancellationRequested();
    }
}
