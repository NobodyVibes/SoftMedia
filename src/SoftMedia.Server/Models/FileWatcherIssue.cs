namespace SoftMedia.Server.Models;

/// <summary>
/// Represents an issue encountered by the file watcher when processing a file.
/// </summary>
public class FileWatcherIssue
{
    public string Path { get; set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Status { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
    public DateTime LastChecked { get; set; }
    public Guid LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public bool CanRetry { get; set; } = true;
}

/// <summary>
/// Status constants for file watcher issues.
/// </summary>
public static class FileWatcherIssueStatus
{
    public const string Locked = "File locked - unable to access";
    public const string Stalled = "Download stalled - no progress";
    public const string Timeout = "Maximum wait time exceeded";
}
