using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Background;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// <summary>
/// Tests for the per-library pending gauge on the image download queue — the second half
/// of what keeps a scan job's Metadata stage open until artwork is actually cached.
/// </summary>
public class ImageDownloadQueueGaugeTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IImageCacheService> _mockImageCache = new();
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ImageDownloadQueueGaugeTests()
    {
        // Cache calls "fail" (return null) so processing early-returns without touching
        // the DB — the gauge must still decrement via the finally block.
        _mockImageCache
            .Setup(c => c.CacheBookPosterAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((string)null!);

        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var provider = new Mock<IServiceProvider>();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            provider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(new AppDbContext(options));
            provider.Setup(x => x.GetService(typeof(IImageCacheService))).Returns(_mockImageCache.Object);
            provider.Setup(x => x.GetService(typeof(IMediaNotificationService))).Returns(new Mock<IMediaNotificationService>().Object);
            scope.Setup(x => x.ServiceProvider).Returns(provider.Object);
            return scope.Object;
        });
    }

    private ImageDownloadQueueService CreateService()
        => new(_mockScopeFactory.Object, new Mock<ILogger<ImageDownloadQueueService>>().Object);

    [Fact]
    public async Task Enqueue_WithLibraryId_IncrementsGauge_WithoutLibraryId_DoesNot()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        await service.EnqueueImageDownloadAsync(Guid.NewGuid(), "http://x/1.jpg", type: MediaType.Book, libraryId: libId);
        await service.EnqueueImageDownloadAsync(Guid.NewGuid(), "http://x/2.jpg", type: MediaType.Book, libraryId: libId);
        await service.EnqueueImageDownloadAsync(Guid.NewGuid(), "http://x/3.jpg", type: MediaType.Book);

        Assert.Equal(2, service.GetPendingCountForLibrary(libId));
    }

    [Fact]
    public async Task Gauge_Drains_WhenDownloadsAreProcessed()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await service.EnqueueImageDownloadAsync(Guid.NewGuid(), "http://x/1.jpg", type: MediaType.Book, libraryId: libId);
            await service.EnqueueImageDownloadAsync(Guid.NewGuid(), "http://x/2.jpg", type: MediaType.Book, libraryId: libId);

            var drained = false;
            for (int i = 0; i < 100; i++)
            {
                if (service.GetPendingCountForLibrary(libId) == 0) { drained = true; break; }
                await Task.Delay(50);
            }

            Assert.True(drained, "Image gauge did not drain after downloads were processed.");
        }
        finally
        {
            cts.Cancel();
        }
    }
}
