using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class EmbeddedMusicProviderTests
{
    private readonly EmbeddedMusicProvider _provider;

    public EmbeddedMusicProviderTests()
    {
        var logger = new Mock<ILogger<EmbeddedMusicProvider>>();
        _provider = new EmbeddedMusicProvider(logger.Object);
    }

    [Fact]
    public async Task FetchMetadataAsync_ReturnsNull_ForAlbumType()
    {
        // Arrange — Albums are directories; TagLib cannot process them
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Some Album",
            Path = "/music/artist/album",
            Type = MediaType.Album
        };

        // Act
        var result = await _provider.FetchMetadataAsync(item);

        // Assert — type guard should short-circuit before TagLib
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_ReturnsNull_ForArtistType()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Some Artist",
            Path = "/music/artist",
            Type = MediaType.Artist
        };

        var result = await _provider.FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_ReturnsNull_ForSeriesType()
    {
        // Guard against wrong library routing — Series items should never reach this provider
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Some TV Show",
            Path = "/tv/show",
            Type = MediaType.Series
        };

        var result = await _provider.FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_ProcessesAudioType_WhenScannedTagsPresent()
    {
        // Arrange — Audio items with pre-scanned tags should be deserialized
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Test Track",
            Path = "/music/artist/album/track.mp3",
            Type = MediaType.Audio,
            MetadataJson = """{"Extra":{"scannedTags":true},"Title":"Test Track","Artist":"Test Artist","Year":2024}"""
        };

        // Act
        var result = await _provider.FetchMetadataAsync(item);

        // Assert — should use pre-scanned metadata path (not TagLib)
        Assert.NotNull(result);
        Assert.Equal("Test Track", result.Title);
        Assert.Equal("Test Artist", result.Artist);
        Assert.Equal(2024, result.Year);
    }
}
