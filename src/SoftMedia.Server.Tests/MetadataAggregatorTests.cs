using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SoftMedia.Server.Tests;

public class MetadataAggregatorTests
{
    private readonly Mock<ISettingsService> _settingsMock;
    private readonly Mock<ILogger<MetadataAggregator>> _loggerMock;
    private readonly Mock<IMetadataProvider> _embeddedProviderMock;
    private readonly Mock<IMetadataProvider> _mbProviderMock;
    private readonly Mock<ImageCacheService> _imageCacheMock;
    private readonly Mock<IMetadataRouter> _metadataRouterMock;
    private readonly AppDbContext _context;
    private readonly List<IMetadataProvider> _providers;
    private readonly MetadataAggregator _aggregator;

    public MetadataAggregatorTests()
    {
        _settingsMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<MetadataAggregator>>();
        
        // Mock ImageCacheService - requires HttpClient, ILogger, and IWebHostEnvironment
        var mockHttpClient = new HttpClient();
        var mockImageLogger = new Mock<ILogger<ImageCacheService>>();
        var mockWebHostEnv = new Mock<IWebHostEnvironment>();
        mockWebHostEnv.Setup(e => e.WebRootPath).Returns(Path.GetTempPath());
        _imageCacheMock = new Mock<ImageCacheService>(mockHttpClient, mockImageLogger.Object, mockWebHostEnv.Object);
        
        _embeddedProviderMock = new Mock<IMetadataProvider>();
        _embeddedProviderMock.Setup(p => p.SupportedType).Returns(LibraryType.Music);
        _embeddedProviderMock.Setup(p => p.ProviderName).Returns("Embedded");

        _mbProviderMock = new Mock<IMetadataProvider>();
        _mbProviderMock.Setup(p => p.SupportedType).Returns(LibraryType.Music);
        _mbProviderMock.Setup(p => p.ProviderName).Returns("MusicBrainz");

        _providers = new List<IMetadataProvider> { _embeddedProviderMock.Object, _mbProviderMock.Object };
        
        _metadataRouterMock = new Mock<IMetadataRouter>();
        // Mock AppDbContext not easily mockable without in-memory options, but lets try null or strict mock if allowed. 
        // Better: use InMemory DB logic like StreamControllerTests?
        // Or just pass null if not used in the test case?
        // EnrichMediaItemAsync MIGHT use context.
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(dbOptions);

        _aggregator = new MetadataAggregator(_providers, _metadataRouterMock.Object, _settingsMock.Object, _imageCacheMock.Object, _context, _loggerMock.Object);

        // Default settings
        _settingsMock.Setup(s => s.GetSettingAsync("MusicProviderPrimary", "Embedded"))
            .ReturnsAsync("Embedded");
        _settingsMock.Setup(s => s.GetSettingAsync("MusicProviderFallback", "MusicBrainz"))
            .ReturnsAsync("MusicBrainz");
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldFetchFromFallback_WhenPrimaryLacksArt()
    {
        // Arrange
        var item = new MediaItem 
        { 
            Title = "Test Track", 
            Path = "/music/artist/album/track.mp3" 
        };

        // Primary (Embedded) returns Title + Artist but NO embedded art
        var embeddedJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "title", "Test Track" },
            { "artist", "Test Artist" }
            // Missing "hasEmbeddedArt"
        });

        _embeddedProviderMock.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()))
            .ReturnsAsync(embeddedJson);

        // Fallback (MusicBrainz) returns Poster
        var mbJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "title", "Test Track" },
            { "artist", "Test Artist" },
            { "poster", "http://example.com/cover.jpg" }
        });

        _mbProviderMock.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()))
            .ReturnsAsync(mbJson);

        // Act
        await _aggregator.EnrichMediaItemAsync(item, LibraryType.Music);

        // Assert
        // Verify fallback was called
        _mbProviderMock.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once, "Fallback provider should have been called");

        // Verify result has poster
        Assert.NotNull(item.MetadataJson);
        var meta = JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
        Assert.NotNull(meta);
        Assert.True(meta.ContainsKey("poster"), "Metadata should contain poster from fallback");
        Assert.Equal("http://example.com/cover.jpg", meta["poster"].ToString());
    }
}
