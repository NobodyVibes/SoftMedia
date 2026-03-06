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
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataAggregatorTests : IDisposable
{
    private readonly Mock<IMetadataProvider> _mockProvider;
    private readonly Mock<IMetadataRouter> _mockRouter;
    private readonly Mock<ISettingsService> _mockSettings;
    private readonly Mock<IImageUrlExtractorService> _mockImageExtractor;
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
        _mockImageExtractor = new Mock<IImageUrlExtractorService>();
        _mockLogger = new Mock<ILogger<MetadataAggregator>>();

        // Default: ExtractAndQueueAsync returns true (images found)
        _mockImageExtractor
            .Setup(x => x.ExtractAndQueueAsync(It.IsAny<MediaItem>(), It.IsAny<Dictionary<string, object>>()))
            .ReturnsAsync(true);
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
            _mockImageExtractor.Object,
            _dbContext,
            _mockLogger.Object);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldCallImageExtractor_WhenPosterUrlPresent()
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
        // 1. Verify ImageUrlExtractorService was called to extract and queue images
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.Is<MediaItem>(m => m.Id == item.Id),
            It.IsAny<Dictionary<string, object>>()), Times.Once);

        // 2. Verify metadata was saved to DB
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.NotNull(savedItem!.MetadataJson);
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
        Assert.Equal("tt1234567", savedItem!.ImdbId);
        Assert.Equal(999, savedItem.TvMazeId);
        Assert.Equal("mb-id-123", savedItem.MusicBrainzId);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldCallImageExtractor_ForSeriesWithSeasons()
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
        // Verify ImageUrlExtractorService was called with the series item
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.Is<MediaItem>(m => m.Id == item.Id && m.Type == MediaType.Series),
            It.IsAny<Dictionary<string, object>>()), Times.Once);
        
        // Verify metadata was saved
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.NotNull(savedItem!.MetadataJson);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldSkipImages_WhenDeferImageCachingIsTrue()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Deferred Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            title = "Deferred Movie",
            poster = "http://example.com/poster.jpg"
        });

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(json);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie, deferImageCaching: true);

        // Assert — Image extractor should NOT be called when deferred
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.IsAny<MediaItem>(),
            It.IsAny<Dictionary<string, object>>()), Times.Never);
    }
}
