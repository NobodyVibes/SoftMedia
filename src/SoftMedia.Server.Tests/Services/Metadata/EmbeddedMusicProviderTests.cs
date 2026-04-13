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
    public async Task FetchMetadataAsync_SkipsAudioType_WhenRecentlyAdded()
    {
        // Arrange — Audio items with recent DateAdded should skip TagLib
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Test Track",
            Path = "/music/artist/album/track.mp3",
            Type = MediaType.Audio,
            DateAdded = DateTime.UtcNow // Recently added!
        };

        // Act
        var result = await _provider.FetchMetadataAsync(item);

        // Assert — should return null immediately without hitting TagLib
        Assert.Null(result);
    }
}
