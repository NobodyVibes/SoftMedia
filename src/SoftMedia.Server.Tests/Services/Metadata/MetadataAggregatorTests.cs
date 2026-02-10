using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataAggregatorTests : IDisposable
{
    private readonly Mock<IMetadataProvider> _mockProvider;
    private readonly Mock<IMetadataRouter> _mockRouter;
    private readonly Mock<ISettingsService> _mockSettings;
    private readonly Mock<IImageDownloadQueue> _mockQueue;
    private readonly Mock<ILogger<MetadataAggregator>> _mockLogger;
    private readonly AppDbContext _dbContext;

    public MetadataAggregatorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _mockProvider = new Mock<IMetadataProvider>();
        _mockRouter = new Mock<IMetadataRouter>();
        _mockSettings = new Mock<ISettingsService>();
        _mockQueue = new Mock<IImageDownloadQueue>();
        _mockLogger = new Mock<ILogger<MetadataAggregator>>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private MetadataAggregator CreateAggregator()
    {
        return new MetadataAggregator(
            new[] { _mockProvider.Object },
            _mockRouter.Object,
            _mockSettings.Object,
            _mockQueue.Object,
            _dbContext,
            _mockLogger.Object);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldEnqueueImage_WhenPosterUrlPresent()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            title = "Test Movie",
            poster = "http://example.com/poster.jpg",
            year = 2023
        });

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(json);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        // Assert
        // 1. Verify Queue was called
        _mockQueue.Verify(x => x.EnqueueImageDownloadAsync(
            item.Id,
            "http://example.com/poster.jpg",
            null,
            null,
            MediaType.Movie,
            ImageType.Poster), Times.Once);

        // 2. Verify Remote URL is removed from saved metadata (No Hotlinking)
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.NotNull(savedItem.MetadataJson);
        Assert.DoesNotContain("http://example.com/poster.jpg", savedItem.MetadataJson);
        Assert.Contains("2023", savedItem.MetadataJson);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPopulateExternalIds()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Show", Type = MediaType.Series };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            title = "Test Show",
            imdbId = "tt1234567",
            tvmazeId = 999,
            musicBrainzId = "mb-id-123"
        });

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.TV))
            .ReturnsAsync(json);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.TV);

        // Assert
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("tt1234567", savedItem.ImdbId);
        Assert.Equal(999, savedItem.TvMazeId);
        Assert.Equal("mb-id-123", savedItem.MusicBrainzId);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPopulateSeasonDiffs_AndQueueSeasonPosters()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Series", Type = MediaType.Series };
        var s1 = new MediaItem { Id = Guid.NewGuid(), SeriesId = item.Id, SeasonNumber = 1, Type = MediaType.Season };
        var s2 = new MediaItem { Id = Guid.NewGuid(), SeriesId = item.Id, SeasonNumber = 2, Type = MediaType.Season };
        
        _dbContext.MediaItems.AddRange(item, s1, s2);
        await _dbContext.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            title = "Test Series",
            seasons = new[]
            {
                new { number = 1, poster = "http://example.com/s1.jpg" },
                new { number = 2, poster = "http://example.com/s2.jpg" }
            }
        });

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.TV))
            .ReturnsAsync(json);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.TV);

        // Assert
        // Verify queues for season posters
        _mockQueue.Verify(x => x.EnqueueImageDownloadAsync(item.Id, "http://example.com/s1.jpg", 1, null, MediaType.Series, ImageType.SeasonPoster), Times.Once);
        _mockQueue.Verify(x => x.EnqueueImageDownloadAsync(item.Id, "http://example.com/s2.jpg", 2, null, MediaType.Series, ImageType.SeasonPoster), Times.Once);
        
        // Verify remote URLs removed
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.DoesNotContain("http://example.com/s1.jpg", savedItem.MetadataJson);
    }
}
