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
    /// <summary>SR-WI-038: scanners skip files whose names could inject ffmpeg arguments
    /// (quotes/control chars). Previously only a log line — now surfaced here so the file
    /// doesn't just silently vanish from the library. Fix: rename the file.</summary>
    public const string UnsafeName = "Skipped - rename needed (unsafe characters in file name)";
}
