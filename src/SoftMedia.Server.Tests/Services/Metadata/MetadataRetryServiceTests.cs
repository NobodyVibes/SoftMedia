using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SR-WI-036 — the persistent retry ladder. The headline regression pinned here: with
/// MaxRetries = 3 the 4-hour tier in BackoffDelays was dead code (exhaustion fired at
/// previousRetries >= 3 before index 3 could be selected), so a ~40-minute provider outage
/// permanently exhausted items. The ladder must be 1m -> 5m -> 30m -> 4h -> exhausted.
/// </summary>
public class MetadataRetryServiceTests
{
    private static (MetadataRetryService svc, ServiceProvider provider, string dbName) Build()
    {
        var services = new ServiceCollection();
        // Hoist the DB name so every scope the service creates sees the same store.
        var dbName = $"retry-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var svc = new MetadataRetryService(NullLogger<MetadataRetryService>.Instance, provider);
        return (svc, provider, dbName);
    }

    private static AppDbContext Db(ServiceProvider provider)
        => provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Theory]
    [InlineData(0, 1)]        // first failure  -> retry in 1 minute
    [InlineData(1, 5)]        // second         -> 5 minutes
    [InlineData(2, 30)]       // third          -> 30 minutes
    [InlineData(3, 4 * 60)]   // fourth         -> 4 HOURS (the previously unreachable tier)
    public async Task EnqueueRetry_UsesFullBackoffLadder_IncludingFourHourTier(
        int previousRetries, int expectedDelayMinutes)
    {
        var (svc, provider, _) = Build();
        var mediaId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem { Id = mediaId, Title = "T", Type = MediaType.Movie, LibraryId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        await svc.EnqueueRetryAsync(mediaId, LibraryType.Movie, previousRetries);

        using (var db = Db(provider))
        {
            var retry = Assert.Single(await db.MetadataRetries.ToListAsync());
            Assert.Equal(previousRetries + 1, retry.RetryCount);

            var expected = TimeSpan.FromMinutes(expectedDelayMinutes);
            var actual = retry.NextAttempt - retry.CreatedAt;
            Assert.InRange(actual, expected - TimeSpan.FromSeconds(10), expected + TimeSpan.FromSeconds(10));

            // The item must NOT be exhausted while rungs remain.
            var item = await db.MediaItems.SingleAsync(m => m.Id == mediaId);
            Assert.False(item.IsRetryExhausted);
        }
    }

    [Fact]
    public async Task EnqueueRetry_AfterFourAttempts_MarksExhausted_AndDropsPendingRow()
    {
        var (svc, provider, _) = Build();
        var mediaId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem { Id = mediaId, Title = "T", Type = MediaType.Movie, LibraryId = Guid.NewGuid() });
            db.MetadataRetries.Add(new MetadataRetry
            {
                MediaItemId = mediaId,
                LibraryType = LibraryType.Movie,
                RetryCount = 4,
                NextAttempt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Fifth failure (previousRetries == MaxRetries == 4): exhaust, don't re-queue.
        await svc.EnqueueRetryAsync(mediaId, LibraryType.Movie, previousRetries: 4);

        using (var db = Db(provider))
        {
            Assert.Empty(await db.MetadataRetries.ToListAsync());
            var item = await db.MediaItems.SingleAsync(m => m.Id == mediaId);
            Assert.True(item.IsRetryExhausted);
        }
    }

    [Fact]
    public async Task EnqueueRetry_ThreeFailures_DoesNotExhaust()
    {
        // Regression pin for the outage scenario: the first three rungs span ~36 minutes,
        // so a ~40-minute provider outage used to exhaust items forever. With the 4h rung
        // reachable, attempt #4 is still scheduled instead of exhausting.
        var (svc, provider, _) = Build();
        var mediaId = Guid.NewGuid();
        using (var db = Db(provider))
        {
            db.MediaItems.Add(new MediaItem { Id = mediaId, Title = "T", Type = MediaType.Movie, LibraryId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        await svc.EnqueueRetryAsync(mediaId, LibraryType.Movie, previousRetries: 3);

        using (var db = Db(provider))
        {
            Assert.Single(await db.MetadataRetries.ToListAsync());
            var item = await db.MediaItems.SingleAsync(m => m.Id == mediaId);
            Assert.False(item.IsRetryExhausted);
        }
    }
}
