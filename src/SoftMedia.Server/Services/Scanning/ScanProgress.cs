namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Represents progress during a library scan operation.
/// </summary>
/// <param name="ProcessedCount">Number of files processed so far.</param>
/// <param name="TotalCount">Total number of files to process.</param>
/// <param name="CurrentFileName">Name of the file currently being processed.</param>
/// <param name="CurrentPhase">Description of the current scan phase.</param>
/// <param name="NewCount">Number of new items added.</param>
/// <param name="UpdatedCount">Number of existing items updated.</param>
/// <param name="SkippedCount">Number of items skipped (unchanged).</param>
public record ScanProgress(
    int ProcessedCount,
    int TotalCount,
    string? CurrentFileName,
    string CurrentPhase,
    int NewCount = 0,
    int UpdatedCount = 0,
    int SkippedCount = 0);
