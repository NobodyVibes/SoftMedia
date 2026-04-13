using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataQueueServiceTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IMetadataAggregator> _mockAggregator;
    private readonly Mock<IMediaNotificationService> _mockNotificationService;
    private readonly Mock<ISettingsService> _mockSettingsService;
    private readonly Mock<ILogger<MetadataQueueService>> _mockLogger;
    private readonly AppDbContext _dbContext;

    public MetadataQueueServiceTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockAggregator = new Mock<IMetadataAggregator>();
        _mockNotificationService = new Mock<IMediaNotificationService>();
        _mockSettingsService = new Mock<ISettingsService>();
        _mockLogger = new Mock<ILogger<MetadataQueueService>>();

        // Setup In-Memory DB
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // Setup Scope Factory
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
        
        // Setup Service Provider
        _mockServiceProvider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(() => new AppDbContext(options)); // Return NEW context per scope? No, InMemory is shared by name? 
        // Actually best to return the same instance for test simplicity if we want to verify state
        _mockServiceProvider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(_dbContext);
        
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMetadataAggregator))).Returns(_mockAggregator.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMediaNotificationService))).Returns(_mockNotificationService.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(ISettingsService))).Returns(_mockSettingsService.Object);
        
        // Need to make GetRequiredService work too since extension method calls GetService
        // But GetRequiredService is an extension method, so it calls GetService internally.
        // Moq supports this.
    }

    [Fact]
    public async Task ProcessQueue_ShouldEnrichItem_AndNotify()
    {
        // Arrange
        var service = new MetadataQueueService(_mockScopeFactory.Object, _mockNotificationService.Object, _mockLogger.Object);
        var mediaId = Guid.NewGuid();
        var item = new MediaItem { Id = mediaId, Title = "Test Movie", Type = MediaType.Movie, LibraryId = Guid.NewGuid() };
        
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockSettingsService.Setup(x => x.GetSettingAsync<string>("MovieProvider", It.IsAny<string>()))
            .ReturnsAsync("Wikidata");

        // Act
        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Movie);
        await service.StartAsync(CancellationToken.None);
        
        // Wait for processing
        await Task.Delay(1000); // Plenty of time for 1 item
        
        await service.StopAsync(CancellationToken.None);

        // Assert
        _mockAggregator.Verify(x => x.EnrichMediaItemAsync(
            It.Is<MediaItem>(m => m.Id == mediaId), 
            LibraryType.Movie, 
            false, 
            true), Times.Once);

        _mockNotificationService.Verify(x => x.NotifyItemUpdated(mediaId), Times.Once);
    }
    [Fact]
    public async Task ProcessItem_RetriesInStrictMode_WhenUnchangedAndMissingDescription()
    {
        // Arrange
        var mockRetryService = new Mock<IMetadataRetryService>();
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMetadataRetryService))).Returns(mockRetryService.Object);
        _mockSettingsService.Setup(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
                            .ReturnsAsync("Strict");
        _mockSettingsService.Setup(x => x.GetSettingAsync<string>("MovieProvider", It.IsAny<string>()))
            .ReturnsAsync("Wikidata");

        var service = new MetadataQueueService(_mockScopeFactory.Object, _mockNotificationService.Object, _mockLogger.Object);
        var mediaId = Guid.NewGuid();
        
        var item = new MediaItem 
        { 
            Id = mediaId, 
            Title = "Test Movie", 
            Type = MediaType.Movie, 
            LibraryId = Guid.NewGuid(),
            PosterUrl = "http://example.com/poster.jpg",
            MetadataHash = "hash123",
            Overview = null // Missing description for strict mode
        };
        
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        // Aggregator does nothing (metadata unchanged)
        _mockAggregator.Setup(x => x.EnrichMediaItemAsync(It.IsAny<MediaItem>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<MediaItem, LibraryType, bool, bool>((m, _, _, _) => {
                // Do not change MetadataHash so it shows up as unchanged
            })
            .Returns(Task.CompletedTask);

        // Act
        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Movie);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100); 
        await service.StopAsync(CancellationToken.None);

        // Assert - should enqueue retry because strict mode requires description
        mockRetryService.Verify(x => x.EnqueueRetryAsync(mediaId, LibraryType.Movie, 0), Times.Once);
    }

    [Fact]
    public async Task ProcessItem_NoRetryInRelaxedMode_WhenUnchangedAndHasPoster()
    {
        // Arrange
        var mockRetryService = new Mock<IMetadataRetryService>();
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMetadataRetryService))).Returns(mockRetryService.Object);
        // Relaxed mode
        _mockSettingsService.Setup(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
                            .ReturnsAsync("Relaxed");
        _mockSettingsService.Setup(x => x.GetSettingAsync<string>("MovieProvider", It.IsAny<string>()))
            .ReturnsAsync("Wikidata");

        var service = new MetadataQueueService(_mockScopeFactory.Object, _mockNotificationService.Object, _mockLogger.Object);
        var mediaId = Guid.NewGuid();
        
        var item = new MediaItem 
        { 
            Id = mediaId, 
            Title = "Test Movie", 
            Type = MediaType.Movie, 
            LibraryId = Guid.NewGuid(),
            PosterUrl = "http://example.com/poster.jpg",
            MetadataHash = "hash123"
        };
        
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        // Aggregator does nothing (metadata unchanged)
        _mockAggregator.Setup(x => x.EnrichMediaItemAsync(It.IsAny<MediaItem>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<MediaItem, LibraryType, bool, bool>((m, _, _, _) => {
                // Do not change MetadataHash
            })
            .Returns(Task.CompletedTask);

        // Act
        await service.EnqueueMetadataRefreshAsync(mediaId, LibraryType.Movie);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100); 
        await service.StopAsync(CancellationToken.None);

        // Assert - should NOT enqueue retry because relaxed mode is satisfied by poster
        mockRetryService.Verify(x => x.EnqueueRetryAsync(It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<int>()), Times.Never);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
