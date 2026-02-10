using System.Threading.Channels;
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

public class ImageDownloadQueueServiceTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IImageCacheService> _mockImageCache;
    private readonly Mock<IMediaNotificationService> _mockNotificationService;
    private readonly Mock<ILogger<ImageDownloadQueueService>> _mockLogger;
    private readonly AppDbContext _dbContext;

    public ImageDownloadQueueServiceTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockImageCache = new Mock<IImageCacheService>();
        _mockNotificationService = new Mock<IMediaNotificationService>();
        _mockLogger = new Mock<ILogger<ImageDownloadQueueService>>();

        // Setup In-Memory DB
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // Setup Scope Factory
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
        
        // Setup Service Provider
        _mockServiceProvider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(() => new AppDbContext(options));
        _mockServiceProvider.Setup(x => x.GetService(typeof(IImageCacheService))).Returns(_mockImageCache.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(IMediaNotificationService))).Returns(_mockNotificationService.Object);
    }

    [Fact]
    public async Task ProcessDownload_ShouldCachePoster_AndNotify()
    {
        // Arrange
        var service = new ImageDownloadQueueService(_mockScopeFactory.Object, _mockLogger.Object);
        var remoteUrl = "http://example.com/poster.jpg";
        var localPath = "/cache/images/movies/test_poster.jpg";
        
        var mediaItem = new MediaItem { 
            Id = Guid.NewGuid(), 
            Title = "Test Movie", 
            Type = MediaType.Movie, 
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { poster = remoteUrl }) 
        };
        _dbContext.MediaItems.Add(mediaItem);
        await _dbContext.SaveChangesAsync();

        _mockImageCache.Setup(x => x.CacheMoviePosterAsync(mediaItem.Id, remoteUrl))
            .ReturnsAsync(localPath);

        // Act
        await service.EnqueueImageDownloadAsync(mediaItem.Id, remoteUrl, null, null, MediaType.Movie, ImageType.Poster);
        await service.StartAsync(CancellationToken.None);
        
        // Wait a bit for processing
        await Task.Delay(500);
        
        await service.StopAsync(CancellationToken.None);

        // Assert
        _mockImageCache.Verify(x => x.CacheMoviePosterAsync(mediaItem.Id, remoteUrl), Times.Once);
        _mockNotificationService.Verify(x => x.NotifyItemUpdated(mediaItem.Id), Times.Once);

        _dbContext.ChangeTracker.Clear();
        var updatedItem = await _dbContext.MediaItems.FindAsync(mediaItem.Id);
        Assert.Contains(localPath, updatedItem!.MetadataJson);
    }
    
    [Fact]
    public async Task ProcessDownload_ShouldCacheSeasonPoster_AndNotify()
    {
        // Arrange
        var service = new ImageDownloadQueueService(_mockScopeFactory.Object, _mockLogger.Object);
        var seriesId = Guid.NewGuid();
        var remoteUrl = "http://example.com/season1.jpg";
        var localPath = "/cache/images/tv/season1.jpg";
        
        var mediaItem = new MediaItem { 
            Id = seriesId, 
            Title = "Test Series", 
            Type = MediaType.Series, 
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { 
                title = "Test Series",
                seasons = new[] {
                    new { number = 1, poster = remoteUrl }
                }
            }) 
        };
        _dbContext.MediaItems.Add(mediaItem);
        
        // Add Season Item
        var seasonItem = new MediaItem { 
            Id = Guid.NewGuid(), 
            SeriesId = seriesId, 
            Type = MediaType.Season, 
            SeasonNumber = 1,
            MetadataJson = "{}" 
        };
        _dbContext.MediaItems.Add(seasonItem);
        await _dbContext.SaveChangesAsync();

        _mockImageCache.Setup(x => x.CacheSeasonPosterAsync(mediaItem.Id, 1, remoteUrl))
            .ReturnsAsync(localPath);

        // Act
        await service.EnqueueImageDownloadAsync(mediaItem.Id, remoteUrl, 1, null, MediaType.Series, ImageType.SeasonPoster);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert
        _mockImageCache.Verify(x => x.CacheSeasonPosterAsync(mediaItem.Id, 1, remoteUrl), Times.Once);
        
        _dbContext.ChangeTracker.Clear();
        // Check Series Metadata updated
        var updatedSeries = await _dbContext.MediaItems.FindAsync(seriesId);
        Assert.Contains(localPath, updatedSeries!.MetadataJson);
        
        // Check Season Metadata updated (via UpdateSeasonEntityMetadata)
        var updatedSeason = await _dbContext.MediaItems.FindAsync(seasonItem.Id);
        Assert.Contains(localPath, updatedSeason!.MetadataJson);
    }
}
