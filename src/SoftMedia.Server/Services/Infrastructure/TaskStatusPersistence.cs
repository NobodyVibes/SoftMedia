using System.Text.Json;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>Serializable snapshot of one task's last-run telemetry.</summary>
public record PersistedTaskStatus(
    string Name,
    DateTime? LastRunUtc,
    string? LastResult,
    long? LastRunDurationMs,
    string? LastError,
    DateTime? NextRunUtc);

/// <summary>
/// Loads/saves background-task telemetry to a small JSON file so the admin Background
/// Tasks card keeps showing the last run/result across a backend reboot. The in-memory
/// <see cref="IScheduledTaskRegistry"/> is still the live source; this just snapshots it.
/// </summary>
public static class TaskStatusStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Default path under the (git-ignored) runtime data directory.</summary>
    public static string DefaultPath()
        => Path.Combine(Directory.GetCurrentDirectory(), "data", "task-status.json");

    /// <summary>Applies persisted telemetry onto already-registered tasks. No-op if absent.</summary>
    public static void Load(IScheduledTaskRegistry registry, string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var statuses = JsonSerializer.Deserialize<List<PersistedTaskStatus>>(json, JsonOpts);
            if (statuses == null) return;
            foreach (var s in statuses)
            {
                if (s.LastRunUtc == null) continue; // nothing meaningful to restore
                registry.LoadPersisted(s.Name, s.LastRunUtc, s.LastResult, s.LastRunDurationMs, s.LastError, s.NextRunUtc);
            }
            logger.LogInformation("Restored {Count} background-task status row(s) from {Path}", statuses.Count, path);
        }
        catch (Exception ex)
        {
            // Telemetry persistence is best-effort: a corrupt/unreadable file must never
            // block startup. The dashboard just shows "never run" until tasks report.
            SafeLogWarning(logger, ex, "Could not load persisted task status from {Path}", path);
        }
    }

    /// <summary>Writes the tasks that have run at least once, atomically.</summary>
    public static void Save(IEnumerable<ScheduledTaskStatus> statuses, string path, ILogger logger)
    {
        // Unique temp name per write so concurrent writers (e.g. parallel test hosts sharing
        // the same data dir) can't collide on it; the final Move(overwrite) is last-writer-wins.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = statuses
                .Where(s => s.LastRunUtc != null)
                .Select(s => new PersistedTaskStatus(s.Name, s.LastRunUtc, s.LastResult, s.LastRunDurationMs, s.LastError, s.NextRunUtc))
                .ToList();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, path, overwrite: true); // atomic replace so a crash can't corrupt it
        }
        catch (Exception ex)
        {
            TryDelete(tmp); // don't leave a stray temp file behind on a failed write
            SafeLogWarning(logger, ex, "Could not persist task status to {Path}", path);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Logs a warning without ever throwing. Persistence runs on graceful shutdown, where a
    /// logger provider (notably the Windows EventLog provider) may already be disposed — and a
    /// throw from logging would abort host shutdown. Best-effort telemetry must not do that.
    /// </summary>
    private static void SafeLogWarning(ILogger logger, Exception ex, string message, params object?[] args)
    {
        try { logger.LogWarning(ex, message, args); }
        catch { /* a disposed logger provider must not crash startup/shutdown */ }
    }
}

/// <summary>
/// Periodically (and on graceful shutdown) snapshots the task registry to disk. Loading
/// happens earlier in startup (Program.cs) so the dashboard is populated before requests.
/// </summary>
public class TaskStatusPersistenceService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private readonly IScheduledTaskRegistry _registry;
    private readonly ILogger<TaskStatusPersistenceService> _logger;
    private readonly string _path = TaskStatusStore.DefaultPath();

    public TaskStatusPersistenceService(IScheduledTaskRegistry registry, ILogger<TaskStatusPersistenceService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(FlushInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            TaskStatusStore.Save(_registry.GetAll(), _path, _logger);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Capture the very latest telemetry on a clean shutdown.
        TaskStatusStore.Save(_registry.GetAll(), _path, _logger);
        await base.StopAsync(cancellationToken);
    }
}
