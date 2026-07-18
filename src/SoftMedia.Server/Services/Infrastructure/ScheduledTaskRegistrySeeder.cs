namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Registers descriptors for every known background task at startup so the admin
/// Background Tasks page is fully populated before any task has run. Schedule
/// classification is honest: queue/watcher services are EventDriven (no NextRun).
/// </summary>
public static class ScheduledTaskRegistrySeeder
{
    public static IDisposable Seed(IScheduledTaskRegistry registry)
    {
        void Sched(string name, string desc, bool manual = false)
            => registry.Register(name, desc, TaskSchedule.Scheduled, manual);
        void Event(string name, string desc)
            => registry.Register(name, desc, TaskSchedule.EventDriven, supportsManualTrigger: false);

        Sched(ScheduledTaskNames.HeroCache, "Rebuilds the home-page hero carousel cache daily.");
        Sched(ScheduledTaskNames.RefreshTokenCleanup, "Prunes expired and revoked refresh tokens daily.");
        Sched(ScheduledTaskNames.MetadataRefresh, "Refreshes metadata for ongoing series on a configurable interval.", manual: true);
        Sched(ScheduledTaskNames.BackupRotation, "Takes a daily database backup and prunes old archives.");
        Sched(ScheduledTaskNames.TranscodeSegmentCleanup, "Hourly: removes transcode session folders whose newest segment is older than the retention window (Settings → Transcoding).");
        Sched(ScheduledTaskNames.ThrottleMonitor, "Monitors transcode buffers and throttles FFmpeg.");
        Sched(ScheduledTaskNames.Trickplay, "Generates scrubber-preview sprite sheets for videos that lack them.");
        Sched(ScheduledTaskNames.ScheduledLibraryScan, "Scans all libraries for new/changed files on a configurable interval (Settings → Libraries). A backstop for changes the realtime file watcher can miss. Disabled when the interval is 0.", manual: true);

        Event(ScheduledTaskNames.LibraryWatcher, "Watches library folders for filesystem changes in real time.");
        Event(ScheduledTaskNames.LibraryScanQueue, "Processes queued library scan jobs.");
        Event(ScheduledTaskNames.MetadataQueue, "Fetches provider metadata for newly-discovered items.");
        Event(ScheduledTaskNames.MetadataRetry, "Retries metadata fetches that previously failed.");
        Event(ScheduledTaskNames.ImageDownloadQueue, "Downloads and caches poster/backdrop images.");

        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
