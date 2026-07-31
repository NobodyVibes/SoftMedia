using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// <summary>
/// SM-WI-026 — a transiently-failed image download retries exactly once (delayed);
/// a second failure gives up. Before this, one network blip left the item
/// hotlink-proxying its remote URL indefinitely.
/// </summary>
public class ImageDownloadQueueRetryTests
{
    private static (ImageDownloadQueueService Service, Mock<IImageCacheService> Cache) CreateService(
        params Func<Task<string>>[] cacheBehaviors)
    {
        var cache = new Mock<IImageCacheService>();
        var call = 0;
        cache.Setup(c => c.CacheMoviePosterAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(() => cacheBehaviors[Math.Min(call++, cacheBehaviors.Length - 1)]());

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => cache.Object);
        services.AddScoped(_ => new AppDbContext(dbOptions));
        services.AddScoped(_ => new Mock<IMediaNotificationService>().Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new ImageDownloadQueueService(
            scopeFactory, NullLogger<ImageDownloadQueueService>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(50),
        };
        return (service, cache);
    }

    [Fact]
    public async Task TransientFailure_RetriesOnce_AndGaugeDrainsToZero()
    {
        var libraryId = Guid.NewGuid();
        var (service, cache) = CreateService(
            () => throw new HttpRequestException("transient"),
            () => Task.FromResult(string.Empty)); // second attempt "succeeds" (no-op result)

        await service.StartAsync(CancellationToken.None);
        await service.EnqueueImageDownloadAsync(
            Guid.NewGuid(), "http://example.com/poster.jpg", null, null,
            MediaType.Movie, ImageType.Poster, libraryId: libraryId);

        // First attempt fails fast; retry fires after ~50 ms.
        await WaitUntilAsync(() => cache.Invocations.Count >= 2, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        cache.Verify(c => c.CacheMoviePosterAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Exactly(2));
        Assert.Equal(0, service.GetPendingCountForLibrary(libraryId)); // gauge balanced
    }

    [Fact]
    public async Task SecondFailure_GivesUp_NoThirdAttempt()
    {
        var libraryId = Guid.NewGuid();
        var (service, cache) = CreateService(
            () => throw new HttpRequestException("transient"),
            () => throw new HttpRequestException("still failing"));

        await service.StartAsync(CancellationToken.None);
        await service.EnqueueImageDownloadAsync(
            Guid.NewGuid(), "http://example.com/poster.jpg", null, null,
            MediaType.Movie, ImageType.Poster, libraryId: libraryId);

        await WaitUntilAsync(() => cache.Invocations.Count >= 2, TimeSpan.FromSeconds(5));
        // Give a would-be third attempt time to (incorrectly) fire.
        await Task.Delay(300);
        await service.StopAsync(CancellationToken.None);

        cache.Verify(c => c.CacheMoviePosterAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Exactly(2));
        Assert.Equal(0, service.GetPendingCountForLibrary(libraryId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition not reached within timeout");
    }
}
