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
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// Tests for the per-library pending-enrichment gauge that keeps a scan job's
/// Metadata stage honest.
/// </summary>
public class MetadataQueueServiceGaugeTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IMediaNotificationService> _mockNotification;
    private readonly Mock<ILogger<MetadataQueueService>> _mockLogger;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public MetadataQueueServiceGaugeTests()
    {
        _mockNotification = new Mock<IMediaNotificationService>();
        _mockLogger = new Mock<ILogger<MetadataQueueService>>();

        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var provider = new Mock<IServiceProvider>();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            provider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(new AppDbContext(options));
            provider.Setup(x => x.GetService(typeof(IMetadataAggregator))).Returns(new Mock<IMetadataAggregator>().Object);
            scope.Setup(x => x.ServiceProvider).Returns(provider.Object);
            return scope.Object;
        });
    }

    private MetadataQueueService CreateService()
        => new(_mockScopeFactory.Object, _mockNotification.Object, _mockLogger.Object);

    [Fact]
    public async Task Enqueue_WithLibraryId_IncrementsGauge()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        await service.EnqueueMetadataRefreshAsync(Guid.NewGuid(), LibraryType.Book, libraryId: libId);
        await service.EnqueueMetadataRefreshAsync(Guid.NewGuid(), LibraryType.Book, libraryId: libId);

        Assert.Equal(2, service.GetPendingCountForLibrary(libId));
        Assert.Equal(0, service.GetPendingCountForLibrary(Guid.NewGuid()));
    }

    [Fact]
    public async Task Enqueue_WithoutLibraryId_DoesNotCount()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        await service.EnqueueMetadataRefreshAsync(Guid.NewGuid(), LibraryType.Book);

        Assert.Equal(0, service.GetPendingCountForLibrary(libId));
    }

    [Fact]
    public async Task Enqueue_DuplicateMediaId_CountsOnce()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Book, libraryId: libId);
        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Book, libraryId: libId);

        Assert.Equal(1, service.GetPendingCountForLibrary(libId));
    }

    [Fact]
    public async Task Gauge_Drains_WhenItemsAreProcessed()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // Items don't exist in the DB, so processing early-returns — the gauge must
            // still decrement via the finally block.
            await service.EnqueueMetadataRefreshAsync(Guid.NewGuid(), LibraryType.Book, libraryId: libId);
            await service.EnqueueMetadataRefreshAsync(Guid.NewGuid(), LibraryType.Book, libraryId: libId);

            var drained = false;
            for (int i = 0; i < 100; i++)
            {
                if (service.GetPendingCountForLibrary(libId) == 0) { drained = true; break; }
                await Task.Delay(50);
            }

            Assert.True(drained, "Gauge did not drain after items were processed.");
        }
        finally
        {
            cts.Cancel();
        }
    }

    public void Dispose()
    {
    }
}
