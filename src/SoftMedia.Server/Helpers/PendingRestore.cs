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
