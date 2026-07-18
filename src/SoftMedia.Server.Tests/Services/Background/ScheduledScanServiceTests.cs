using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// R-WI-008 — scheduled periodic library scans. Covers the pure due-decision (the scheduler
/// loop's only branching logic), the enqueue-all run (every library enqueued, outcome reported
/// to the registry, failures contained), and the interval setting's seed.
public class ScheduledScanServiceTests
{
    private static readonly Guid MoviesId = Guid.NewGuid();
    private static readonly Guid TvId = Guid.NewGuid();

    private static (ScheduledScanService svc, Mock<ILibraryScanQueueService> queue, ScheduledTaskRegistry registry)
        BuildService(bool queueThrows = false)
    {
        var queue = new Mock<ILibraryScanQueueService>();
        if (queueThrows)
        {
            queue.Setup(q => q.EnqueueScan(It.IsAny<Guid>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("queue unavailable"));
        }

        var services = new ServiceCollection();
        // Hoist the DB name: the options lambda runs per scope, so an inline NewGuid would give
        // every scope its own (empty) database and the service would never see the seeded rows.
        var dbName = $"sched-scan-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(queue.Object);
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Libraries.Add(new Library { Id = MoviesId, Name = "Movies", Type = LibraryType.Movie });
            db.Libraries.Add(new Library { Id = TvId, Name = "TV", Type = LibraryType.TV });
            db.SaveChanges();
        }

        var registry = new ScheduledTaskRegistry();
        registry.Register(ScheduledTaskNames.ScheduledLibraryScan, "test", TaskSchedule.Scheduled, supportsManualTrigger: true);

        var svc = new ScheduledScanService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScheduledScanService>.Instance,
            registry);
        return (svc, queue, registry);
    }

    // --- IsDue: the scheduler's run decision ---

    [Fact]
    public void IsDue_DisabledInterval_NeverDue()
    {
        Assert.False(ScheduledScanService.IsDue(null, null, 0, DateTime.UtcNow));
        Assert.False(ScheduledScanService.IsDue(DateTime.UtcNow.AddDays(-30), "Success", 0, DateTime.UtcNow));
        Assert.False(ScheduledScanService.IsDue(null, null, -5, DateTime.UtcNow));
        // Disabled wins even over a failed last attempt — an admin turning the schedule off stops retries.
        Assert.False(ScheduledScanService.IsDue(DateTime.UtcNow, "Failed", 0, DateTime.UtcNow));
    }

    [Fact]
    public void IsDue_NeverRanWhileEnabled_DueImmediately()
    {
        // First-time enable: run promptly rather than waiting a full interval.
        Assert.True(ScheduledScanService.IsDue(null, null, 24, DateTime.UtcNow));
    }

    [Fact]
    public void IsDue_IntervalElapsed_Due_ElseNot()
    {
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(ScheduledScanService.IsDue(now.AddHours(-25), "Success", 24, now));  // overdue (incl. across reboots)
        Assert.True(ScheduledScanService.IsDue(now.AddHours(-24), "Success", 24, now));  // exactly due
        Assert.False(ScheduledScanService.IsDue(now.AddHours(-23), "Success", 24, now)); // not yet
    }

    [Fact]
    public void IsDue_LastAttemptFailed_RetriesInsteadOfWaitingFullInterval()
    {
        // Review finding (R-WI-008): Report stamps LastRunUtc for FAILURES too, so anchoring only
        // on the timestamp would let one transient failure (e.g. SQLite locked by the nightly
        // backup at the due moment) silently defer the backstop by a whole interval — and the
        // deferral would survive reboots via task-status persistence. A Failed last result must
        // stay due (the loop paces the retry to its check period).
        var now = DateTime.UtcNow;
        Assert.True(ScheduledScanService.IsDue(now.AddMinutes(-1), "Failed", 24, now));
    }

    // --- EnqueueAllLibraries / TriggerNow: the run itself ---

    [Fact]
    public void TriggerNow_EnqueuesScanForEveryLibrary_AndReportsSuccess()
    {
        var (svc, queue, registry) = BuildService();

        svc.TriggerNow();

        queue.Verify(q => q.EnqueueScan(MoviesId, "Movies"), Times.Once);
        queue.Verify(q => q.EnqueueScan(TvId, "TV"), Times.Once);
        queue.VerifyNoOtherCalls();

        var status = registry.GetAll().Single(t => t.Name == ScheduledTaskNames.ScheduledLibraryScan);
        Assert.Equal("Success", status.LastResult);
        Assert.NotNull(status.LastRunUtc); // the anchor the schedule (and persistence) relies on
    }

    [Fact]
    public void TriggerNow_WhenEnqueueFails_ReportsFailed_AndDoesNotThrow()
    {
        var (svc, _, registry) = BuildService(queueThrows: true);

        var ex = Record.Exception(() => svc.TriggerNow()); // 202 semantics: failures surface on the tasks page
        Assert.Null(ex);

        var status = registry.GetAll().Single(t => t.Name == ScheduledTaskNames.ScheduledLibraryScan);
        Assert.Equal("Failed", status.LastResult);
        Assert.Contains("queue unavailable", status.LastError);
    }

    [Fact]
    public void EnqueueAllLibraries_PartialFailure_StillEnqueuesTheRest_AndReportsFailed()
    {
        // Review finding (R-WI-008): a throwing EnqueueScan must not abort the batch — the
        // remaining libraries still get their backstop scan, and the Failed report makes the
        // scheduler retry at the check period (dedup makes that retry a no-op for these).
        var (svc, queue, registry) = BuildService();
        queue.Setup(q => q.EnqueueScan(MoviesId, "Movies"))
            .Throws(new InvalidOperationException("boom"));

        var result = svc.EnqueueAllLibraries();

        Assert.Equal(-1, result); // failure signalled so the loop paces a retry
        queue.Verify(q => q.EnqueueScan(TvId, "TV"), Times.Once); // batch continued past the failure

        var status = registry.GetAll().Single(t => t.Name == ScheduledTaskNames.ScheduledLibraryScan);
        Assert.Equal("Failed", status.LastResult);
        Assert.Contains("1 of 2 libraries failed", status.LastError);
        Assert.Contains("Movies: boom", status.LastError);
    }

    // --- Queue dedup atomicity (concurrency review finding) ---

    [Fact]
    public void EnqueueScan_ParallelCallsForSameLibrary_YieldExactlyOneJob()
    {
        // The scheduler sweep and an admin Run-now can enqueue the same library concurrently.
        // The dedup check + insert must be atomic, or the library gets fully scanned twice
        // (doubled I/O and duplicate scan-completed webhooks). Uses the REAL queue service —
        // EnqueueScan is synchronous and doesn't need the background loop.
        var queue = new LibraryScanQueueService(
            new Mock<IServiceScopeFactory>().Object,
            NullLogger<LibraryScanQueueService>.Instance,
            Mock.Of<IWebhookDispatcher>());

        var libraryId = Guid.NewGuid();
        var jobs = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 64, _ => jobs.Add(queue.EnqueueScan(libraryId, "Racy").Id));

        Assert.Single(jobs.Distinct()); // every caller got the SAME job
        Assert.Single(queue.GetAllJobs(), j => j.LibraryId == libraryId);
    }

    // --- Setting seed ---

    [Fact]
    public async Task InitializeDefaults_SeedsLibraryScanIntervalHours_DisabledByDefault()
    {
        var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sched-scan-seed-{Guid.NewGuid()}").Options);
        var settings = new SettingsService(ctx, NullLogger<SettingsService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        await settings.InitializeDefaultsAsync();

        var seeded = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == ScheduledScanService.IntervalSettingKey);
        Assert.NotNull(seeded);
        Assert.Equal("0", seeded!.Value);        // off by default — preserves current behaviour
        Assert.Equal("Scanning", seeded.Group);  // renders in the Libraries → Scanning section
    }
}
