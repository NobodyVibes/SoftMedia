using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// Periodically backfills trickplay sprite sheets for video items that lack them
/// (P2-WI-001). A sweep model (rather than hooking scan completion) is self-healing:
/// it covers pre-existing libraries and regenerates if the cache is cleared.
///
/// Generation is gated by the worker's OWN semaphore (not the transcode concurrency
/// cap, which is only reachable inside StartTranscodeAsync) so trickplay never
/// competes with user-initiated transcodes.
/// </summary>
public class TrickplayWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private const int MaxPerSweep = 25; // bound work per cycle so a huge library trickles in

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrickplayWorker> _logger;
    private readonly IScheduledTaskRegistry _registry;
    // BG-WI-003: one generation at a time. Keyframe-only decode (BG-WI-001) made a
    // generation sub-second CPU, so concurrency no longer buys throughput — it only
    // doubled worst-case pressure (the measured 2026-07-24 saturation was 2 unbounded
    // decodes running concurrently). The sweep cadence still clears a season per cycle.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrickplayWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TrickplayWorker> logger,
        IScheduledTaskRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var enabled = await GetEnabledAsync();
                if (enabled) await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrickplayWorker sweep failed");
                _registry.Report(ScheduledTaskNames.Trickplay, "Failed", error: ex.Message);
            }

            _registry.SetNextRun(ScheduledTaskNames.Trickplay, DateTime.UtcNow.Add(SweepInterval));
            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> GetEnabledAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        return await settings.GetSettingAsync("TrickplayEnabled", true);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Candidate video items: Movies + Episodes with a real file path. Pull a
        // bounded recent slice from the DB, then filter to those missing trickplay.
        List<(Guid Id, string Path)> candidates;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trickplay = scope.ServiceProvider.GetRequiredService<ITrickplayService>();

            var recent = await db.MediaItems
                .AsNoTracking()
                .Where(m => (m.Type == MediaType.Movie || m.Type == MediaType.Episode) && m.Path != "")
                .OrderByDescending(m => m.DateAdded)
                .Select(m => new { m.Id, m.Path })
                .Take(500) // cap the per-sweep scan
                .ToListAsync(ct);

            candidates = recent
                .Where(x => !trickplay.HasTrickplay(x.Id))
                .Take(MaxPerSweep)
                .Select(x => (x.Id, x.Path))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            _registry.Report(ScheduledTaskNames.Trickplay, "Skipped", sw.ElapsedMilliseconds);
            return;
        }

        var generated = 0;
        long cpuMs = 0; // BG-WI-004: ffmpeg CPU actually burned this sweep (successes and failures)
        var tasks = candidates.Select(async item =>
        {
            await _gate.WaitAsync(ct);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var trickplay = scope.ServiceProvider.GetRequiredService<ITrickplayService>();
                var result = await trickplay.GenerateAsync(item.Id, item.Path, ct);
                Interlocked.Add(ref cpuMs, (long)(result.CpuSeconds * 1000));
                if (result.Success)
                    Interlocked.Increment(ref generated);
            }
            finally { _gate.Release(); }
        });
        await Task.WhenAll(tasks);

        _logger.LogInformation("Trickplay sweep generated {Count} sheet sets ({CpuSeconds:F1}s ffmpeg CPU)",
            generated, cpuMs / 1000.0);
        _registry.Report(ScheduledTaskNames.Trickplay,
            $"Success — {generated} generated, {cpuMs / 1000.0:F1}s ffmpeg CPU", sw.ElapsedMilliseconds);
    }
}
