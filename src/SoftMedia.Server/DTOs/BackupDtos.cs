namespace SoftMedia.Server.DTOs;

/// <summary>
/// Summary of a single backup artefact on disk, returned by the history endpoint.
/// </summary>
public record BackupInfo(
    string Id,
    DateTime CreatedAtUtc,
    long SizeBytes,
    bool IsPinned);

/// <summary>
/// Result of staging an uploaded restore. <see cref="RestartRequired"/> is always
/// true on success: the actual file swap happens on the next process start, before
/// the database connection opens (see <see cref="SoftMedia.Server.Helpers.PendingRestore"/>).
/// </summary>
public record RestoreStageResult(
    bool Success,
    string Message,
    bool RestartRequired);
