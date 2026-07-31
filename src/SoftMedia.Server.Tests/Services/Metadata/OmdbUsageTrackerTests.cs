using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class OmdbUsageTrackerTests
{
    private static (OmdbUsageTracker Tracker, Dictionary<string, string> Store) CreateTracker(
        Func<DateTime>? utcNow = null,
        Dictionary<string, string>? seed = null)
    {
        var store = seed ?? new Dictionary<string, string>();

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string key, string def) => store.TryGetValue(key, out var v) ? v : def);
        settings.Setup(s => s.UpdateSettingsAsync(It.IsAny<List<AppSetting>>()))
            .Returns((List<AppSetting> list) =>
            {
                foreach (var setting in list) store[setting.Key] = setting.Value;
                return Task.CompletedTask;
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => settings.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var tracker = new OmdbUsageTracker(
            scopeFactory,
            new Mock<ILogger<OmdbUsageTracker>>().Object,
            utcNow ?? (() => new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)));

        return (tracker, store);
    }

    [Fact]
    public async Task TryRecordRequestAsync_ConcurrentCalls_CountsEveryRequest()
    {
        // The metadata queue fetches movies with up to 10 in parallel; the old
        // read-modify-write counter lost increments under exactly this load.
        var (tracker, store) = CreateTracker();

        var results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(_ => Task.Run(() => tracker.TryRecordRequestAsync(1_000))));

        Assert.All(results, Assert.True);
        Assert.Equal(40, await tracker.GetUsedTodayAsync());
        Assert.Equal("40", store["OMDbDailyCount"]);
    }

    [Fact]
    public async Task TryRecordRequestAsync_PersistsInBatches_NotPerRequest()
    {
        // SM-WI-025: the old per-request write pair serialized OMDb traffic through the
        // settings table. In-memory stays exact; persistence lands on batch boundaries.
        var (tracker, store) = CreateTracker();

        for (var i = 0; i < 25; i++)
        {
            Assert.True(await tracker.TryRecordRequestAsync(1_000));
        }

        Assert.Equal(25, await tracker.GetUsedTodayAsync());
        Assert.Equal("20", store["OMDbDailyCount"]); // last batch boundary, not 25
    }

    [Fact]
    public async Task MarkExhaustedAsync_BlocksSameDay_ResetsAtUtcMidnight()
    {
        // SM-WI-011: an OMDb-reported quota/key refusal suspends calls for the rest of
        // the UTC day, and the suspension lifts on rollover.
        var now = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var (tracker, store) = CreateTracker(() => now);

        Assert.True(await tracker.TryRecordRequestAsync(1_000));
        await tracker.MarkExhaustedAsync(1_000);

        Assert.False(await tracker.TryRecordRequestAsync(1_000));
        Assert.Equal(1_000, await tracker.GetUsedTodayAsync());
        Assert.Equal("1000", store["OMDbDailyCount"]); // persisted so a restart stays suspended

        now = now.AddDays(1); // UTC midnight passed
        Assert.True(await tracker.TryRecordRequestAsync(1_000));
        Assert.Equal(1, await tracker.GetUsedTodayAsync());
    }

    [Fact]
    public async Task TryRecordRequestAsync_AtLimit_RefusesWithoutCounting()
    {
        var (tracker, _) = CreateTracker();

        var granted = 0;
        for (var i = 0; i < 5; i++)
        {
            if (await tracker.TryRecordRequestAsync(3)) granted++;
        }

        Assert.Equal(3, granted);
        Assert.Equal(3, await tracker.GetUsedTodayAsync());
    }

    [Fact]
    public async Task Counter_ResetsOnUtcDateRollover()
    {
        var now = new DateTime(2026, 7, 20, 23, 0, 0, DateTimeKind.Utc);
        var (tracker, store) = CreateTracker(() => now);

        Assert.True(await tracker.TryRecordRequestAsync(10));
        Assert.True(await tracker.TryRecordRequestAsync(10));
        Assert.Equal(2, await tracker.GetUsedTodayAsync());

        now = now.AddDays(1);

        Assert.Equal(0, await tracker.GetUsedTodayAsync());
        Assert.True(await tracker.TryRecordRequestAsync(10));
        Assert.Equal(1, await tracker.GetUsedTodayAsync());

        // SM-WI-025: persistence is batched (every 10th increment / limit boundary), so
        // the rolled-over date lands in the store at the next boundary — drive to it.
        // A crash before that is safe: restart loads the stale date and rolls over again.
        for (var i = 0; i < 9; i++) Assert.True(await tracker.TryRecordRequestAsync(20));
        Assert.Equal("2026-07-21", store["OMDbCountDate"]);
    }

    [Fact]
    public async Task Tracker_RestoresPersistedCountForToday()
    {
        var (tracker, _) = CreateTracker(seed: new Dictionary<string, string>
        {
            ["OMDbDailyCount"] = "5",
            ["OMDbCountDate"] = "2026-07-20"
        });

        Assert.Equal(5, await tracker.GetUsedTodayAsync());
    }

    [Fact]
    public async Task Tracker_DiscardsPersistedCountFromPreviousDay()
    {
        var (tracker, _) = CreateTracker(seed: new Dictionary<string, string>
        {
            ["OMDbDailyCount"] = "999",
            ["OMDbCountDate"] = "2026-07-19"
        });

        Assert.Equal(0, await tracker.GetUsedTodayAsync());
        Assert.True(await tracker.TryRecordRequestAsync(1_000));
    }
}
