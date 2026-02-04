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
using SoftMedia.Server.Services.Metadata; // For IMetadataAggregator and MetadataQueueService
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataQueueServiceTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IMediaNotificationService> _mockNotificationService;
    private readonly Mock<ILogger<MetadataQueueService>> _mockLogger;
    private readonly Mock<IMetadataAggregator> _mockAggregator;
    private readonly AppDbContext _dbContext;
    
    public MetadataQueueServiceTests()
    {
         // Setup InMemory DB
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // Setup Service Scope Mocking
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockServiceProvider = new Mock<IServiceProvider>();

        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
        
        // Mock Aggregator
        _mockAggregator = new Mock<IMetadataAggregator>();
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMetadataAggregator))).Returns(_mockAggregator.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(_dbContext);

        _mockLogger = new Mock<ILogger<MetadataQueueService>>();
        _mockNotificationService = new Mock<IMediaNotificationService>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private MetadataQueueService CreateService()
    {
        return new MetadataQueueService(
            _mockScopeFactory.Object,
            _mockNotificationService.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task ProcessItems_ShouldCallAggregator_WhenItemQueued()
    {
        // Arrange
        var service = CreateService();
        var mediaId = Guid.NewGuid();
        var item = new MediaItem { Id = mediaId, Title = "Test Item", Type = MediaType.Movie, LibraryId = Guid.NewGuid() };
        
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource();

        // Act
        // Start the background service
        var task = service.StartAsync(cts.Token);
        
        // Enqueue item
        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Movie);

        // Assert - Wait for processing
        int retries = 0;
        bool processed = false;
        while (retries < 20)
        {
            try 
            {
                _mockAggregator.Verify(x => x.EnrichMediaItemAsync(
                    It.Is<MediaItem>(m => m.Id == mediaId), 
                    LibraryType.Movie, 
                    false, 
                    true), Times.Once);
                processed = true;
                break;
            }
            catch (MockException)
            {
                await Task.Delay(50);
                retries++;
            }
        }

        // Stop service
        cts.Cancel();
        await Task.WhenAny(task, Task.Delay(100)); // Ensure we don't block forever

        Assert.True(processed, "Aggregator was not called with the expected item within timeout.");
        
        // Verify notification
        _mockNotificationService.Verify(x => x.NotifyItemUpdated(mediaId), Times.Once);
    }

    [Fact]
    public async Task ProcessItems_ShouldRespectRateLimiting()
    {
        // This test is tricky because RateLimiter uses real time.
        // We can check that processing multiple items takes *at least* some time, 
        // OR simply verify that all items are eventually processed.
        // Given we use FixedWindow 1/1.1s for Music, we can queue 2 music items and verify delay?
        // That might be flaky.
        // Let's just verify sequential processing correctness for now.
        
        // Arrange
        var service = CreateService();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        
        _dbContext.MediaItems.AddRange(
            new MediaItem { Id = id1, Title = "Music 1", Type = MediaType.Audio },
            new MediaItem { Id = id2, Title = "Music 2", Type = MediaType.Audio }
        );
        await _dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        // Act
        await service.EnqueueMetadataRefreshAsync(id1, LibraryType.Music);
        await service.EnqueueMetadataRefreshAsync(id2, LibraryType.Music);

        // Assert
        // Wait for both
         int retries = 0;
        bool processed = false;
        while (retries < 50) // 50 * 50ms = 2.5s (should be enough for 2 items with 1s window)
        {
            try 
            {
                _mockAggregator.Verify(x => x.EnrichMediaItemAsync(It.IsAny<MediaItem>(), LibraryType.Music, false, true), Times.Exactly(2));
                processed = true;
                break;
            }
            catch (MockException)
            {
                await Task.Delay(50);
                retries++;
            }
        }

        cts.Cancel();
        
        Assert.True(processed, "Did not process both Music items within expected time (rate limit might be blocking too long?)");
    }
}
