using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Represents progress during a library scan operation.
/// </summary>
/// <param name="ProcessedCount">Number of files processed so far.</param>
/// <param name="TotalCount">Total number of files to process. -1 while still being discovered.</param>
/// <param name="CurrentFileName">Name of the file currently being processed.</param>
/// <param name="CurrentPhase">Description of the current scan phase.</param>
/// <param name="NewCount">Number of new items added.</param>
/// <param name="UpdatedCount">Number of existing items updated.</param>
/// <param name="SkippedCount">Number of items skipped (unchanged).</param>
/// <param name="ErrorCount">Number of files that failed processing.</param>
/// <param name="Stage">Which stage of the scan is running.</param>
public record ScanProgress(
    int ProcessedCount,
    int TotalCount,
    string? CurrentFileName,
    string CurrentPhase,
    int NewCount = 0,
    int UpdatedCount = 0,
    int SkippedCount = 0,
    int ErrorCount = 0,
    LibraryScanStage Stage = LibraryScanStage.Processing);
