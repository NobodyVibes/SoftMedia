using Microsoft.Data.Sqlite;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// On the first boot after a database restore, re-fetches artwork that the restore
/// could not bring back. Backups deliberately exclude the on-disk image cache
/// (<c>wwwroot/cache</c>), so a restored database references <c>/cache/...</c> files
/// that don't exist on this host. <see cref="PendingRestore.Apply"/> drops a marker
/// when it swaps a database in; this service consumes that marker exactly once and
/// runs <see cref="IArtworkRepairService"/> to re-queue the affected items.
/// </summary>
public class ArtworkRepairOnRestoreService : BackgroundService
{
    // Brief settle delay so the metadata/image queues and DB are fully up before we
    // enqueue a burst of re-enrichment work.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ArtworkRepairOnRestoreService> _logger;

    public ArtworkRepairOnRestoreService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<ArtworkRepairOnRestoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The ENTIRE body is guarded: an unhandled exception from ExecuteAsync faults the
        // BackgroundService and (with the .NET default StopHost behaviour) tears down the
        // whole application. Path/IO probing below must never be able to do that.
        try
        {
            var dbPath = ResolveDbPath();
            if (dbPath == null) return;

            var marker = PendingRestore.AppliedMarkerPath(dbPath);
            if (!File.Exists(marker)) return; // no restore was applied this boot

            await Task.Delay(SettleDelay, stoppingToken);

            using var scope = _scopeFactory.CreateScope();
            var repair = scope.ServiceProvider.GetRequiredService<IArtworkRepairService>();
            var result = await repair.RepairAsync(stoppingToken);

            _logger.LogWarning(
                "Post-restore artwork repair: re-queued {ReEnqueued} item(s) ({Missing} missing references, {Locked} locked skipped, {Rescan} need re-scan, {Failed} failed to enqueue).",
                result.ItemsReEnqueued, result.MissingImages, result.LockedSkipped, result.NeedsRescan, result.FailedEnqueue);

            // Keep the marker for a retry next boot if there was work to do but nothing
            // got queued (e.g. the metadata queue rejected every enqueue). The sweep is
            // self-limiting: once art is restored, a re-run simply finds nothing missing.
            var totalFailure = result.MissingImages > 0 && result.ItemsReEnqueued == 0 && result.FailedEnqueue > 0;
            if (totalFailure)
            {
                _logger.LogError("Post-restore artwork repair queued nothing despite {Missing} missing references; leaving the marker to retry next boot.", result.MissingImages);
                return;
            }

            // Consume the marker now that the sweep ran.
            try { File.Delete(marker); }
            catch (Exception ex) { _logger.LogError(ex, "Could not remove restore-applied marker {Marker}; artwork repair may re-run next boot (harmless — it will find nothing missing).", marker); }
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the sweep finished — leave the marker for next boot.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-restore artwork repair failed; will retry on next start.");
        }
    }

    /// <summary>
    /// Resolves the live database path the same way <c>Program.cs</c> does, so the
    /// marker location matches where <see cref="PendingRestore.Apply"/> wrote it.
    /// </summary>
    private string? ResolveDbPath()
    {
        try
        {
            var connString = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connString)) return null;
            var dataSource = new SqliteConnectionStringBuilder(connString).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource)) return null;
            // Non-file sources like ":memory:" (used in tests) aren't real paths and have
            // no artwork to repair; Path.GetFullPath may throw on them — caught below.
            if (dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
                || dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
        }
        catch
        {
            return null; // unparseable / non-file connection string — skip auto-repair
        }
    }
}
