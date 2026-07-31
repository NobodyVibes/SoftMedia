using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// DV-WI-010 — one-shot boot sweep that assigns <c>VersionGroupId</c> to rows scanned
/// before version groups existed, and heals gaps the scan-time paths can leave (two
/// copies first seen by parallel workers in one scan; provider-id conflicts surfaced by
/// later enrichment). Idempotent by construction — assignment is fill-only, so a
/// converged database writes nothing — and therefore simply runs every boot, same
/// philosophy as <see cref="ChapterMarkerBackfillService"/>. Admin splits (fresh ids)
/// are non-null and thus never touched.
/// </summary>
public class VersionGroupBackfillService : BackgroundService
{
    // Let the DB/migrations settle before touching MediaItems.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VersionGroupBackfillService> _logger;

    public VersionGroupBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<VersionGroupBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fully guarded: a fault here must never tear down the host.
        try
        {
            await Task.Delay(SettleDelay, stoppingToken);
            var (episodes, movies, reconciled) = await RunOnceAsync(stoppingToken);
            if (episodes + movies + reconciled > 0)
            {
                _logger.LogInformation(
                    "Version-group backfill: grouped {Episodes} episode row(s), {Movies} movie row(s); reconciled {Reconciled} watched flag(s).",
                    episodes, movies, reconciled);
            }
            else
            {
                _logger.LogDebug("Version-group backfill: already converged.");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown before the sweep finished — next boot repeats it harmlessly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Version-group backfill failed; will retry on next start.");
        }
    }

    /// <summary>
    /// Core sweep, separated from the hosted-service plumbing so tests can drive it
    /// directly (project convention; no InternalsVisibleTo).
    /// </summary>
    public async Task<(int Episodes, int Movies, int WatchedReconciled)> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var episodes = await VersionGroupAssigner.AssignEpisodeGroupsAsync(db, ct);
        var movies = await VersionGroupAssigner.GroupMoviesAsync(db, libraryId: null, ct);
        if (episodes + movies > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // DV-WI-014: legacy interactions predate the write fan-out — align watched flags
        // inside every multi-member group (any-watched wins).
        var reconciled = await VersionGroupAssigner.ReconcileGroupWatchedAsync(db, onlyGroupIds: null, ct);
        if (reconciled > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return (episodes, movies, reconciled);
    }
}
