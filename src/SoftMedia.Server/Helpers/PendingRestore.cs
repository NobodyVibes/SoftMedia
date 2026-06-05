namespace SoftMedia.Server.Helpers;

/// <summary>
/// Applies a database restore that the admin restore endpoint staged on a prior
/// run. The endpoint writes the validated backup database to
/// <c>&lt;db&gt;.restore-pending</c> and returns "restart required" rather than
/// replacing the live file while connections are open (which is unreliable on
/// Windows and risks corrupting an in-flight WAL). The swap happens here, on the
/// next boot, BEFORE the DbContext opens the database.
/// </summary>
public static class PendingRestore
{
    public const string PendingSuffix = ".restore-pending";

    /// <summary>
    /// Marker written next to the database after a restore is applied on boot. A
    /// background service consumes it to trigger a one-shot artwork-repair sweep
    /// (backups exclude the image cache, so restored rows point at missing files).
    /// </summary>
    public const string RestoreAppliedSuffix = ".restore-applied";

    /// <summary>Path of the restore-applied marker for a given database path.</summary>
    public static string AppliedMarkerPath(string dbPath) => dbPath + RestoreAppliedSuffix;

    /// <summary>
    /// If a pending-restore file exists next to <paramref name="dbPath"/>, move
    /// the current database aside (preserved as <c>.pre-restore-*</c>), discard
    /// any stale WAL/SHM sidecars, and swap the pending file into place. No-op if
    /// no pending file exists.
    /// </summary>
    public static void Apply(string dbPath, ILogger logger)
    {
        var pending = dbPath + PendingSuffix;
        if (!File.Exists(pending)) return;

        try
        {
            if (File.Exists(dbPath))
            {
                var preRestore = $"{dbPath}.pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(dbPath, preRestore, overwrite: true);
                logger.LogWarning("Pending restore: preserved current database as {Path}", preRestore);
            }

            // Stale WAL/SHM sidecars belong to the OLD database; leaving them would
            // let SQLite merge old write-ahead-log frames into the restored file.
            foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }

            File.Move(pending, dbPath, overwrite: true);
            logger.LogWarning("Pending restore applied: {Pending} -> {Db}", pending, dbPath);

            // Signal the post-restore artwork-repair sweep. Backups exclude the image
            // cache, so the restored database references /cache/ files that don't exist
            // here; the marker lets a background service re-fetch that art once on boot.
            try { File.WriteAllText(AppliedMarkerPath(dbPath), DateTime.UtcNow.ToString("O")); }
            catch (Exception markerEx)
            {
                // The marker is what triggers automatic artwork repair on this boot. If we
                // can't write it, the auto-repair won't run — surface that loudly so an
                // operator knows to click "Repair Artwork" manually.
                logger.LogError(markerEx,
                    "Restore applied but could not write the artwork-repair marker at {Path}. " +
                    "Automatic artwork repair will NOT run — trigger it manually from the admin dashboard.",
                    AppliedMarkerPath(dbPath));
            }
        }
        catch (Exception ex)
        {
            // Do not throw: a failed swap must not prevent the server from booting
            // on the (preserved) prior database. The pending file is left in place
            // for manual inspection.
            logger.LogError(ex, "Failed to apply pending restore from {Pending}", pending);
        }
    }
}
