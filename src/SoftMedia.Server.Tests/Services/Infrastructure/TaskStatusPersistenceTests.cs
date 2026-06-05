using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Verifies background-task telemetry survives a reboot: it round-trips to disk and is
/// restored VERBATIM (the original run time, not "now"), only for tasks that ran and are
/// still registered.
public class TaskStatusPersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TaskStatusPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sm-taskstatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "task-status.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SaveThenLoad_RestoresTelemetryVerbatim()
    {
        var ranAt = new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc);

        var before = new ScheduledTaskRegistry();
        before.Register(ScheduledTaskNames.BackupRotation, "d", TaskSchedule.Scheduled, false);
        before.LoadPersisted(ScheduledTaskNames.BackupRotation, ranAt, "Success", 1234, null, null);

        TaskStatusStore.Save(before.GetAll(), _path, NullLogger.Instance);

        // Fresh boot: descriptors re-seeded, telemetry empty until restore.
        var after = new ScheduledTaskRegistry();
        after.Register(ScheduledTaskNames.BackupRotation, "d", TaskSchedule.Scheduled, false);
        Assert.Null(after.GetAll().Single().LastRunUtc);

        TaskStatusStore.Load(after, _path, NullLogger.Instance);

        var status = after.GetAll().Single();
        Assert.Equal(ranAt, status.LastRunUtc); // exact time preserved, not stamped "now"
        Assert.Equal("Success", status.LastResult);
        Assert.Equal(1234, status.LastRunDurationMs);
    }

    [Fact]
    public void Save_OmitsNeverRunTasks()
    {
        var reg = new ScheduledTaskRegistry();
        reg.Register(ScheduledTaskNames.HeroCache, "d", TaskSchedule.Scheduled, false); // never run

        TaskStatusStore.Save(reg.GetAll(), _path, NullLogger.Instance);

        var fresh = new ScheduledTaskRegistry();
        fresh.Register(ScheduledTaskNames.HeroCache, "d", TaskSchedule.Scheduled, false);
        TaskStatusStore.Load(fresh, _path, NullLogger.Instance);

        Assert.Null(fresh.GetAll().Single().LastResult); // still never run
    }

    [Fact]
    public void Load_IgnoresTasksNotRegisteredThisBoot()
    {
        var before = new ScheduledTaskRegistry();
        before.Register(ScheduledTaskNames.Trickplay, "d", TaskSchedule.Scheduled, false);
        before.LoadPersisted(ScheduledTaskNames.Trickplay, DateTime.UtcNow, "Skipped", null, null, null);
        TaskStatusStore.Save(before.GetAll(), _path, NullLogger.Instance);

        // A boot that no longer registers Trickplay must not resurrect it.
        var after = new ScheduledTaskRegistry();
        after.Register(ScheduledTaskNames.BackupRotation, "d", TaskSchedule.Scheduled, false);
        TaskStatusStore.Load(after, _path, NullLogger.Instance);

        Assert.DoesNotContain(after.GetAll(), t => t.Name == ScheduledTaskNames.Trickplay);
    }

    [Fact]
    public void Load_MissingFile_IsNoOp()
    {
        var reg = new ScheduledTaskRegistry();
        reg.Register(ScheduledTaskNames.BackupRotation, "d", TaskSchedule.Scheduled, false);
        TaskStatusStore.Load(reg, _path, NullLogger.Instance); // file doesn't exist yet
        Assert.Null(reg.GetAll().Single().LastRunUtc);
    }
}
