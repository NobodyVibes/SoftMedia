using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// <summary>
/// SR-WI-036 — weekly retry amnesty. An IsRetryExhausted item was previously stuck forever
/// (nothing cleared the flag); the amnesty pass must clear it and re-enqueue the item through
/// the central metadata queue, while never touching locked items and never spending provider
/// quota on missing ones.
/// </summary>
public class MetadataRetryAmnestyServiceTests
{
    private static (MetadataRetryAmnestyService svc, Mock<IMetadataQueue> queue,
        ScheduledTaskRegistry registry, ServiceProvider provider) Build()
    {
        var queue = new Mock<IMetadataQueue>();
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
            .ReturnsAsync("Relaxed");

        var services = new ServiceCollection();
        // Hoist the DB name: the options lambda runs per scope; an inline NewGuid would give
        // every scope its own empty database.
        var dbName = $"amnesty-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(queue.Object);
        services.AddSingleton(settings.Object);
        var provider = services.BuildServiceProvider();

        var registry = new ScheduledTaskRegistry();
        registry.Register(ScheduledTaskNames.MetadataRetryAmnesty, "test", TaskSchedule.Scheduled, supportsManualTrigger: true);

        var svc = new MetadataRetryAmnestyService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MetadataRetryAmnestyService>.Instance,
            registry);
        return (svc, queue, registry, provider);
    }

    private static AppDbContext Db(ServiceProvider provider)
        => provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task RunAmnesty_ClearsExhaustedFlag_AndReEnqueuesThroughTheQueue()
    {
        var (svc, queue, registry, provider) = Build();
        var movieId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            // Poster-less + hash-less: still needs enrichment once the flag clears.
            db.MediaItems.Add(new MediaItem { Id = movieId, Title = "Stuck Movie", Type = MediaType.Movie, LibraryId = Guid.NewGuid(), IsRetryExhausted = true });
            db.MediaItems.Add(new MediaItem { Id = albumId, Title = "Stuck Album", Type = MediaType.Album, LibraryId = Guid.NewGuid(), IsRetryExhausted = true });
            // Stale bookkeeping must go too, so the ladder restarts at tier 1.
            db.MetadataRetries.Add(new MetadataRetry { MediaItemId = movieId, LibraryType = LibraryType.Movie, RetryCount = 4, NextAttempt = DateTime.UtcNow.AddHours(4), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var enqueued = await svc.RunAmnestyAsync();

        Assert.Equal(2, enqueued);
        using (var db = Db(provider))
        {
            Assert.Empty(await db.MediaItems.Where(m => m.IsRetryExhausted).ToListAsync());
            Assert.Empty(await db.MetadataRetries.ToListAsync());
        }
        // Enqueue goes through the central queue (rate limiting + lock re-check live there),
        // with the library type derived from the media type — not the movie-else-TV shortcut.
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(movieId, LibraryType.Movie, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(albumId, LibraryType.Music, true, 0, null), Times.Once);

        var status = registry.GetAll().Single(t => t.Name == ScheduledTaskNames.MetadataRetryAmnesty);
        Assert.Equal("Success", status.LastResult);
        Assert.NotNull(status.LastRunUtc);
    }

    [Fact]
    public async Task RunAmnesty_NeverTouchesLockedItems()
    {
        var (svc, queue, _, provider) = Build();
        var lockedId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem
            {
                Id = lockedId, Title = "Locked", Type = MediaType.Movie, LibraryId = Guid.NewGuid(),
                IsRetryExhausted = true, MetadataLocked = true, MetadataLockedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var enqueued = await svc.RunAmnestyAsync();

        Assert.Equal(0, enqueued);
        using (var db = Db(provider))
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == lockedId);
            Assert.True(item.IsRetryExhausted); // untouched — an explicit admin match wins
        }
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task RunAmnesty_ClearsMissingItems_ButDoesNotEnqueueThem()
    {
        var (svc, queue, _, provider) = Build();
        var missingId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem
            {
                Id = missingId, Title = "Gone", Type = MediaType.Movie, LibraryId = Guid.NewGuid(),
                IsRetryExhausted = true, IsMissing = true, MissingSinceUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var enqueued = await svc.RunAmnestyAsync();

        Assert.Equal(0, enqueued);
        using (var db = Db(provider))
        {
            var item = await db.MediaItems.SingleAsync(m => m.Id == missingId);
            Assert.False(item.IsRetryExhausted); // flag amnestied, so a future heal retries normally
        }
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task RunAmnesty_SkipsItemsThatNoLongerNeedEnrichment()
    {
        // Exhausted but complete (provider poster + hash): clear the flag, but don't waste
        // queue/provider capacity re-enriching it.
        var (svc, queue, _, provider) = Build();
        var doneId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem
            {
                Id = doneId, Title = "Complete", Type = MediaType.Movie, LibraryId = Guid.NewGuid(),
                IsRetryExhausted = true, PosterUrl = "http://example.com/p.jpg", MetadataHash = "h",
            });
            await db.SaveChangesAsync();
        }

        var enqueued = await svc.RunAmnestyAsync();

        Assert.Equal(0, enqueued);
        using (var db = Db(provider))
        {
            Assert.False((await db.MediaItems.SingleAsync(m => m.Id == doneId)).IsRetryExhausted);
        }
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    // --- IsDue: the scheduler's run decision (mirrors ScheduledScanServiceTests) ---

    [Fact]
    public void IsDue_NeverRan_DueImmediately()
        => Assert.True(MetadataRetryAmnestyService.IsDue(null, null, DateTime.UtcNow));

    [Fact]
    public void IsDue_WeekElapsed_Due_ElseNot()
    {
        var now = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(MetadataRetryAmnestyService.IsDue(now.AddDays(-8), "Success", now));
        Assert.True(MetadataRetryAmnestyService.IsDue(now.AddDays(-7), "Success", now));
        Assert.False(MetadataRetryAmnestyService.IsDue(now.AddDays(-6), "Success", now));
    }

    [Fact]
    public void IsDue_LastAttemptFailed_RetriesInsteadOfWaitingAWeek()
        => Assert.True(MetadataRetryAmnestyService.IsDue(DateTime.UtcNow.AddMinutes(-1), "Failed", DateTime.UtcNow));
}
