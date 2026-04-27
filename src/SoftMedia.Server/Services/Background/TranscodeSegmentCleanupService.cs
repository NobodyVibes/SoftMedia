using SoftMedia.Server.Services.Transcoding;

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
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;
    private readonly ILogger<TranscodeSegmentCleanupService> _logger;

    public TranscodeSegmentCleanupService(
        IServiceProvider services,
        ILogger<TranscodeSegmentCleanupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait one interval before the first tick so we don't race against the
        // startup-time cleanup that TranscodeService already does.
        try
        {
            await Task.Delay(TickInterval, stoppingToken);
        }
        catch (TaskCanceledException) { return; }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                // Never let an exception kill the background loop.
                _logger.LogError(ex, "Transcode segment cleanup tick threw");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// Tick body. Public + virtual to allow direct exercise from tests without
    /// having to drive the timer.
    public virtual void RunOnce()
    {
        using var scope = _services.CreateScope();
        var transcodeService = scope.ServiceProvider.GetRequiredService<ITranscodeService>();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ITranscodeSessionManager>();

        var root = transcodeService.GetTempDir();
        if (!Directory.Exists(root))
        {
            return;
        }

        var openDirs = new HashSet<string>(
            sessionManager.GetAllSessions()
                .Select(s => SafeFullPath(s.SessionDirectory))
                .Where(p => !string.IsNullOrEmpty(p))!,
            StringComparer.OrdinalIgnoreCase);

        var canonicalRoot = SafeFullPath(root);
        if (canonicalRoot == null)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        var deletedCount = 0;
        long freedBytes = 0;

        foreach (var dir in Directory.EnumerateDirectories(canonicalRoot))
        {
            var canonicalDir = SafeFullPath(dir);
            if (canonicalDir == null) continue;

            // Belt-and-braces: never act outside the configured temp root.
            // If GetFullPath ever returns something that escapes, skip it.
            if (!canonicalDir.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (openDirs.Contains(canonicalDir))
            {
                continue;
            }

            DateTime lastWrite;
            try { lastWrite = Directory.GetLastWriteTimeUtc(canonicalDir); }
            catch { continue; }

            if (lastWrite > cutoff)
            {
                continue;
            }

            try
            {
                freedBytes += DirectorySize(canonicalDir);
                Directory.Delete(canonicalDir, recursive: true);
                deletedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to delete stale transcode session dir {Dir}: {Message}",
                    canonicalDir, ex.Message);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Transcode segment cleanup: removed {Count} stale session(s), freed ~{KB} KB",
                deletedCount, freedBytes / 1024);
        }
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
