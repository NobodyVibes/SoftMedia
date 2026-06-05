using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Background;

/// SDD §4.5: a background worker MUST aggressively clean up old HLS segments to
/// prevent the transcode-temp directory from growing unboundedly when clients
/// disconnect without calling DELETE /api/transcode/{id}.
///
/// On every tick (default 5 min) this service:
///   1. Asks <see cref="ITranscodeSessionManager"/> for the set of session
///      directories belonging to currently-open sessions.
///   2. Walks the immediate children of <see cref="ITranscodeService.GetTempDir"/>.
///   3. Deletes any child directory whose absolute path is not in the open set
///      AND whose <c>LastWriteTime</c> is older than <see cref="StaleAfter"/>
///      (default 10 min). Only walks the configured temp root, never above it.
///
/// Startup-time cleanup of <c>transcode-temp</c> is already handled by
/// <see cref="TranscodeService"/> (it nukes the whole directory on construction);
/// this worker is the long-running counterpart for live processes.
public class TranscodeSegmentCleanupService : BackgroundService
{
    // Hourly sweep. Session folders are KEPT after the player closes so playback can
    // resume quickly; this prunes folders whose newest segment is older than the
    // configurable retention (Transcoding.SegmentRetentionHours, default 24h).
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private const int DefaultRetentionHours = 24;

    private readonly IServiceProvider _services;
    private readonly ILogger<TranscodeSegmentCleanupService> _logger;
    private readonly IScheduledTaskRegistry? _registry;

    public TranscodeSegmentCleanupService(
        IServiceProvider services,
        ILogger<TranscodeSegmentCleanupService> logger,
        IScheduledTaskRegistry? registry = null)
    {
        _services = services;
        _logger = logger;
        _registry = registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (TaskCanceledException) { return; }

        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await RunOnceAsync(stoppingToken);
                _registry?.Report(ScheduledTaskNames.TranscodeSegmentCleanup, "Success", sw.ElapsedMilliseconds,
                    nextRunUtc: DateTime.UtcNow.Add(SweepInterval));
            }
            catch (Exception ex)
            {
                // Never let an exception kill the background loop.
                _logger.LogError(ex, "Transcode segment cleanup tick threw");
                _registry?.Report(ScheduledTaskNames.TranscodeSegmentCleanup, "Failed", sw.ElapsedMilliseconds, ex.Message,
                    nextRunUtc: DateTime.UtcNow.Add(SweepInterval));
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// Sweep body. Public + virtual so tests can drive it without the timer.
    /// Deletes each session folder whose newest segment is at least the configured
    /// retention old, EXCEPT folders of live (transcoding/throttled) sessions. When a
    /// folder backing a dormant/completed session is removed, that session is also
    /// evicted from the manager so it isn't left pointing at a deleted directory.
    public virtual async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var transcodeService = scope.ServiceProvider.GetRequiredService<ITranscodeService>();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ITranscodeSessionManager>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var root = transcodeService.GetTempDir();
        if (!Directory.Exists(root)) return;

        var canonicalRoot = SafeFullPath(root);
        if (canonicalRoot == null) return;

        var retentionHours = await settings.GetSettingAsync("SegmentRetentionHours", DefaultRetentionHours);
        var retention = TimeSpan.FromHours(Math.Max(0, retentionHours));

        // Live sessions (FFmpeg running or throttled) are never touched. Dormant/Completed
        // sessions are eligible for age-based pruning, and are evicted when removed.
        var sessions = sessionManager.GetAllSessions().ToList();
        var liveDirs = new HashSet<string>(
            sessions.Where(s => s.State is TranscodeState.Transcoding or TranscodeState.Throttled)
                .Select(s => SafeFullPath(s.SessionDirectory)).Where(p => p != null)!,
            StringComparer.OrdinalIgnoreCase);
        var dirToKey = new Dictionary<string, TranscodeSessionKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions.Where(s => s.State is TranscodeState.Dormant or TranscodeState.Completed))
        {
            var p = SafeFullPath(s.SessionDirectory);
            if (p != null) dirToKey[p] = s.Key;
        }

        var now = DateTime.UtcNow;
        var deletedCount = 0;
        long freedBytes = 0;

        foreach (var dir in Directory.EnumerateDirectories(canonicalRoot))
        {
            ct.ThrowIfCancellationRequested();
            var canonicalDir = SafeFullPath(dir);
            if (canonicalDir == null) continue;

            // Belt-and-braces: never act outside the configured temp root.
            if (!canonicalDir.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (liveDirs.Contains(canonicalDir)) continue;

            var newest = NewestFileUtc(canonicalDir) ?? SafeLastWriteUtc(canonicalDir);
            if (newest == null) continue;
            if (now - newest.Value < retention) continue;

            try
            {
                freedBytes += DirectorySize(canonicalDir);
                Directory.Delete(canonicalDir, recursive: true);
                deletedCount++;

                // Drop the dormant/completed session so it isn't left dangling.
                if (dirToKey.TryGetValue(canonicalDir, out var key))
                {
                    sessionManager.TryRemoveSession(key, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to delete expired transcode session dir {Dir}: {Message}",
                    canonicalDir, ex.Message);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Transcode segment cleanup: removed {Count} session folder(s) older than {Hours}h, freed ~{KB} KB",
                deletedCount, retentionHours, freedBytes / 1024);
        }
    }

    /// Newest last-write time across all files in a session folder, or null if empty.
    private static DateTime? NewestFileUtc(string dir)
    {
        DateTime? newest = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(f); }
                catch { continue; }
                if (newest == null || t > newest) newest = t;
            }
        }
        catch { return null; }
        return newest;
    }

    private static DateTime? SafeLastWriteUtc(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir); }
        catch { return null; }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
