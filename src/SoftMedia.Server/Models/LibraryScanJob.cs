namespace SoftMedia.Server.Models;

/// <summary>
/// Represents the current status of a library scan job.
/// </summary>
public enum LibraryScanStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Represents the current stage of a library scan.
/// </summary>
public enum LibraryScanJobType
{
    LibraryScan,
    MetadataRefresh,
    /// <summary>
    /// Cross-episode intro / end-credits fingerprint detection for a single series.
    /// Series id is carried on <see cref="LibraryScanJob.TargetSeriesId"/>.
    /// </summary>
    IntroCreditsDetection
}

public enum LibraryScanStage
{
    Pending,
    Discovery,
    Processing,
    Metadata,
    Finishing
}

/// <summary>
/// Tracks the state and progress of a library scan or metadata refresh operation.
/// </summary>
public class LibraryScanJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LibraryScanJobType Type { get; set; } = LibraryScanJobType.LibraryScan;
    
    // Nullable for global jobs like Metadata Refresh
    public Guid LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Series id for series-scoped jobs (currently only <see cref="LibraryScanJobType.IntroCreditsDetection"/>).
    /// Null for library- or global-scoped jobs.
    /// </summary>
    public Guid? TargetSeriesId { get; set; }
    public LibraryScanStatus Status { get; set; } = LibraryScanStatus.Queued;
    public LibraryScanStage Stage { get; set; } = LibraryScanStage.Pending;
    
    /// <summary>
    /// Total number of media files discovered for scanning.
    /// </summary>
    public int TotalFiles { get; set; }
    
    /// <summary>
    /// Number of files processed so far.
    /// </summary>
    public int ProcessedFiles { get; set; }
    
    /// <summary>
    /// Number of new media items added to the library.
    /// </summary>
    public int NewItems { get; set; }
    
    /// <summary>
    /// Number of existing media items updated.
    /// </summary>
    public int UpdatedItems { get; set; }
    
    /// <summary>
    /// Number of files skipped (already up to date or not media files).
    /// </summary>
    public int SkippedItems { get; set; }
    
    /// <summary>
    /// Number of errors encountered during scanning.
    /// </summary>
    public int ErrorCount { get; set; }
    
    /// <summary>
    /// The file path currently being processed.
    /// </summary>
    public string? CurrentFile { get; set; }
    
    /// <summary>
    /// Error message if the scan failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// When the scan was queued/started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the scan completed (success or failure).
    /// </summary>
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Position in the queue (1 = next to run, 0 = currently running or completed).
    /// </summary>
    public int QueuePosition { get; set; }
    
    /// <summary>
    /// Calculates progress percentage (0-100).
    /// </summary>
    public int ProgressPercent => TotalFiles > 0 ? (int)((ProcessedFiles * 100.0) / TotalFiles) : 0;
}
