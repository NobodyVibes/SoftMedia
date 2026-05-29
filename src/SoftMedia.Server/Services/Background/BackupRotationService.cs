using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// Runs a daily database backup at the configured local time and prunes old
/// archives per the retention policy. Reads its schedule/retention from the
/// Maintenance.* settings each cycle so admin changes take effect without restart.
///
/// AppDbContext / IBackupService / ISettingsService are all scoped, so a fresh
/// scope is created per cycle (the queue-service idiom used elsewhere in
/// AddBackgroundServices), never injected into this singleton's constructor.
/// </summary>
public class BackupRotationService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackupRotationService> _logger;
    private DateTime _lastRunDateLocal = DateTime.MinValue;

    public BackupRotationService(IServiceScopeFactory scopeFactory, ILogger<BackupRotationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll every few minutes and fire once when local time passes the configured
        // HH:mm and we have not already run today. Cheap, restart-safe, and avoids
        // pinning the loop to a precise timer that drifts across DST.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MaybeRunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackupRotationService cycle failed.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task MaybeRunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetSettingAsync("Maintenance.BackupEnabled", true);
        if (!enabled) return;

        var schedule = await settings.GetSettingAsync("Maintenance.BackupSchedule", "04:00");
        if (!TimeOnly.TryParse(schedule, out var runAt)) runAt = new TimeOnly(4, 0);

        var now = DateTime.Now;
        var today = now.Date;
        var alreadyRanToday = _lastRunDateLocal == today;
        if (alreadyRanToday || TimeOnly.FromDateTime(now) < runAt) return;

        var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var registry = scope.ServiceProvider.GetRequiredService<IScheduledTaskRegistry>();
        var retentionDaily = await settings.GetSettingAsync("Maintenance.BackupRetentionDaily", 7);
        var retentionWeekly = await settings.GetSettingAsync("Maintenance.BackupRetentionWeekly", 4);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var info = await backup.CreateBackupAsync(ct);
            _lastRunDateLocal = today;
            await backup.PruneAsync(retentionDaily, retentionWeekly, ct);
            _logger.LogInformation("Scheduled backup completed: {Id} ({Size} bytes).", info.Id, info.SizeBytes);
            registry.Report(ScheduledTaskNames.BackupRotation, "Success", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            registry.Report(ScheduledTaskNames.BackupRotation, "Failed", sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }
}
