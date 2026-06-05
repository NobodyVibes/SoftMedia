using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Services.Abstractions;

public interface IBackupService
{
    /// <summary>
    /// Produces a consistent SoftMedia backup zip in the configured backup directory
    /// and returns its metadata. Safe under concurrent writers (SQLite online-backup API).
    /// </summary>
    Task<BackupInfo> CreateBackupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="CreateBackupAsync(CancellationToken)"/>, but also sets an editable
    /// display name. A null/blank name leaves the backup labelled by its id.
    /// </summary>
    Task<BackupInfo> CreateBackupAsync(string? name, CancellationToken cancellationToken);

    /// <summary>Lists backups (newest first) across the main and pinned directories.</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets (or, for a null/blank name, clears) a backup's editable display name. The
    /// archive itself is untouched. Returns false if the id does not resolve to a backup.
    /// </summary>
    Task<bool> SetBackupNameAsync(string id, string? name, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently deletes a backup archive and its sidecar markers (pin + name).
    /// Returns false if the id does not resolve to a backup.
    /// </summary>
    Task<bool> DeleteBackupAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a backup zip for download. Returns null if the id does not resolve to a
    /// backup file inside the backup directory (id is validated against path traversal).
    /// </summary>
    Task<Stream?> OpenBackupAsync(string id, CancellationToken cancellationToken);

    /// <summary>Pins or unpins a backup so rotation never deletes it. Returns false if not found.</summary>
    Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken);

    /// <summary>
    /// Validates an uploaded backup zip and stages its database for restore on next
    /// process start. Does NOT mutate the live database in-process.
    /// </summary>
    Task<RestoreStageResult> StageRestoreAsync(Stream uploadedZip, CancellationToken cancellationToken);

    /// <summary>Deletes unpinned backups beyond the retention window. Returns the count deleted.</summary>
    Task<int> PruneAsync(int retentionDaily, int retentionWeekly, CancellationToken cancellationToken);
}
