using System.Collections.Concurrent;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>How a background task is driven, so the UI can be honest about NextRun.</summary>
public enum TaskSchedule
{
    /// Runs on a clock/interval and has a meaningful next-run time.
    Scheduled,
    /// Driven by filesystem/queue events; "next run" is not predictable.
    EventDriven,
}

/// <summary>A point-in-time view of one background task's status.</summary>
public class ScheduledTaskStatus
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TaskSchedule Schedule { get; init; }
    public bool SupportsManualTrigger { get; init; }

    public DateTime? LastRunUtc { get; set; }
    public long? LastRunDurationMs { get; set; }
    public string? LastResult { get; set; }   // "Success" | "Failed" | "Skipped"
    public string? LastError { get; set; }
    public DateTime? NextRunUtc { get; set; }
}

/// <summary>
/// In-memory registry of background tasks and their last-run telemetry (P1-WI-005).
/// Services register a descriptor at startup and call <see cref="Report"/> each cycle.
/// Singleton; survives across requests. Reporting is additive — it never alters a
/// service's existing IHostedService behaviour.
/// </summary>
public interface IScheduledTaskRegistry
{
    void Register(string name, string description, TaskSchedule schedule, bool supportsManualTrigger);
    void Report(string name, string result, long? durationMs = null, string? error = null, DateTime? nextRunUtc = null);
    void SetNextRun(string name, DateTime? nextRunUtc);
    IReadOnlyList<ScheduledTaskStatus> GetAll();
}

public class ScheduledTaskRegistry : IScheduledTaskRegistry
{
    private readonly ConcurrentDictionary<string, ScheduledTaskStatus> _tasks = new();

    public void Register(string name, string description, TaskSchedule schedule, bool supportsManualTrigger)
        => _tasks.AddOrUpdate(name,
            _ => new ScheduledTaskStatus
            {
                Name = name, Description = description, Schedule = schedule, SupportsManualTrigger = supportsManualTrigger,
            },
            (_, existing) =>
            {
                // Preserve runtime telemetry across a re-registration.
                existing.LastRunUtc ??= existing.LastRunUtc;
                return existing;
            });

    public void Report(string name, string result, long? durationMs = null, string? error = null, DateTime? nextRunUtc = null)
    {
        if (!_tasks.TryGetValue(name, out var status)) return;
        status.LastRunUtc = DateTime.UtcNow;
        status.LastResult = result;
        status.LastRunDurationMs = durationMs;
        status.LastError = error;
        if (nextRunUtc.HasValue) status.NextRunUtc = nextRunUtc;
    }

    public void SetNextRun(string name, DateTime? nextRunUtc)
    {
        if (_tasks.TryGetValue(name, out var status)) status.NextRunUtc = nextRunUtc;
    }

    public IReadOnlyList<ScheduledTaskStatus> GetAll()
        => _tasks.Values.OrderBy(t => t.Name).ToList();
}

/// <summary>Canonical task names — shared between services and registration so they match.</summary>
public static class ScheduledTaskNames
{
    public const string HeroCache = "Hero Cache Refresh";
    public const string RefreshTokenCleanup = "Refresh Token Cleanup";
    public const string MetadataRefresh = "Metadata Refresh";
    public const string BackupRotation = "Database Backup";
    public const string TranscodeSegmentCleanup = "Transcode Segment Cleanup";
    public const string LibraryWatcher = "Library File Watcher";
    public const string LibraryScanQueue = "Library Scan Queue";
    public const string MetadataQueue = "Metadata Queue";
    public const string MetadataRetry = "Metadata Retry";
    public const string ImageDownloadQueue = "Image Download Queue";
    public const string ThrottleMonitor = "Transcode Throttle Monitor";
    public const string Trickplay = "Trickplay Generation";
}
