using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Background;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// <summary>
/// CM-WI-003: the boot-time sweep applies chapter markers to already-scanned items —
/// including overriding Detected values (Chapter is ground truth) — while leaving
/// chapterless items untouched, and is idempotent across runs.
/// </summary>
public class ChapterMarkerBackfillServiceTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ChapterMarkerBackfillService NewService(AppDbContext db)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(AppDbContext))).Returns(db);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return new ChapterMarkerBackfillService(factory.Object, NullLogger<ChapterMarkerBackfillService>.Instance);
    }

    private static MediaItem AddEpisode(AppDbContext db, double duration, params (double Start, string Title)[] chapters)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Episode,
            Title = "Ep",
            Path = @"C:\tv\ep.mkv",
            Duration = duration,
        };
        foreach (var (start, title) in chapters)
            item.Chapters.Add(new Chapter { MediaItemId = item.Id, StartTime = start, Title = title });
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item;
    }

    [Fact]
    public async Task Backfill_MapsChapteredItems_AndOverridesDetected()
    {
        await using var db = NewDb();
        var chaptered = AddEpisode(db, 1486.736, (0, "Intro"), (32.324, "Scene 1"), (1437.853, "Credits"));
        chaptered.IntroStart = 9.66; chaptered.IntroEnd = 34.79; chaptered.IntroSource = DetectionSource.Detected;
        await db.SaveChangesAsync();

        var chapterless = AddEpisode(db, 1400);
        chapterless.IntroStart = 10; chapterless.IntroEnd = 40; chapterless.IntroSource = DetectionSource.Detected;
        await db.SaveChangesAsync();

        var (checkedCount, updated) = await NewService(db).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, checkedCount); // only the chaptered item is a candidate
        Assert.Equal(1, updated);

        var refreshed = await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == chaptered.Id);
        Assert.Equal(0, refreshed.IntroStart);
        Assert.Equal(32.324, refreshed.IntroEnd);
        Assert.Equal(DetectionSource.Chapter, refreshed.IntroSource);
        Assert.Equal(1437.853, refreshed.CreditsStart);
        Assert.Equal(1486.736, refreshed.CreditsEnd);
        Assert.Equal(DetectionSource.Chapter, refreshed.CreditsSource);

        var untouched = await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == chapterless.Id);
        Assert.Equal(10, untouched.IntroStart);
        Assert.Equal(DetectionSource.Detected, untouched.IntroSource);
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        await using var db = NewDb();
        AddEpisode(db, 1486.736, (0, "Intro"), (32.324, "Scene 1"), (1437.853, "Credits"));

        var service = NewService(db);
        var first = await service.RunOnceAsync(CancellationToken.None);
        var second = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.Updated);
        Assert.Equal(0, second.Updated); // same values recomputed → nothing written
    }

    [Fact]
    public async Task Backfill_ClearsChapterSourcedValues_TheMapperNowRejects()
    {
        // A previously-written Chapter-sourced marker whose stored chapters no longer map
        // (rule tightened — e.g. the span caps) must be cleared, not left as a stale skip
        // target. Detected values in the other segment survive.
        await using var db = NewDb();
        var item = AddEpisode(db, 1352.384,
            (0, "Scene 1"), (54.012, "Opening Credits"), (525.025, "Scene 3"), (1314.146, "End Credits"));
        item.IntroStart = 54.012; item.IntroEnd = 525.025; item.IntroSource = DetectionSource.Chapter;
        await db.SaveChangesAsync();

        var (_, updated) = await NewService(db).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, updated);
        var refreshed = await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == item.Id);
        Assert.Null(refreshed.IntroStart);
        Assert.Null(refreshed.IntroEnd);
        Assert.Null(refreshed.IntroSource);
        Assert.Equal(DetectionSource.Chapter, refreshed.CreditsSource); // valid credits chapter still applied
        Assert.Equal(1314.146, refreshed.CreditsStart);
    }

    [Fact]
    public async Task Backfill_LeavesItems_WhoseChaptersMatchNothing_Alone()
    {
        await using var db = NewDb();
        var item = AddEpisode(db, 1400, (0, "Chapter 1"), (700, "Chapter 2"));
        item.CreditsStart = 1350; item.CreditsSource = DetectionSource.Detected;
        await db.SaveChangesAsync();

        var (_, updated) = await NewService(db).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        var refreshed = await db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == item.Id);
        Assert.Equal(1350, refreshed.CreditsStart); // detected value survives
        Assert.Equal(DetectionSource.Detected, refreshed.CreditsSource);
    }
}
