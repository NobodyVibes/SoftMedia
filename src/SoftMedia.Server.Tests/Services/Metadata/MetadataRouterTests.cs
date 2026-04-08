using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataRouterTests
{
    private readonly Mock<ISettingsService> _mockSettings;
    private readonly Mock<ILogger<MetadataRouter>> _mockLogger;

    public MetadataRouterTests()
    {
        _mockSettings = new Mock<ISettingsService>();
        _mockLogger = new Mock<ILogger<MetadataRouter>>();

        // Default: MusicBrainz mode (embedded primary + MusicBrainz fallback)
        _mockSettings.Setup(s => s.GetSettingAsync("MusicProvider", "MusicBrainz"))
            .ReturnsAsync("MusicBrainz");
        _mockSettings.Setup(s => s.GetSettingAsync("MusicProviderPrimary", It.IsAny<string>()))
            .ReturnsAsync("Embedded");
        _mockSettings.Setup(s => s.GetSettingAsync("MusicProviderFallback", It.IsAny<string>()))
            .ReturnsAsync("MusicBrainz");
    }

    private MetadataRouter CreateRouter(params IMetadataProvider[] providers)
    {
        return new MetadataRouter(providers, _mockSettings.Object, _mockLogger.Object);
    }

    private static Mock<IMetadataProvider> CreateMockProvider(string name, LibraryType type, MetadataResult? result = null)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.SupportedType).Returns(type);
        mock.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()))
            .ReturnsAsync(result);
        return mock;
    }

    // ---- Track sufficiency tests ----

    [Fact]
    public async Task FetchMusicMetadata_TrackSufficient_WhenHasTitleArtistAndArt()
    {
        // Arrange — Track with all required fields from embedded provider
        var result = new MetadataResult { Title = "Song", Artist = "Band", HasEmbeddedArt = true };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, result);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Song", Type = MediaType.Audio };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — sufficient, so fallback should NOT be called
        Assert.NotNull(fetched);
        Assert.Equal("Song", fetched.Title);
        musicBrainz.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task FetchMusicMetadata_TrackInsufficient_WhenMissingArtist()
    {
        // Arrange — Track has title + art but missing artist
        var primaryResult = new MetadataResult { Title = "Song", HasEmbeddedArt = true };
        var fallbackResult = new MetadataResult { Title = "Song", Artist = "Band" };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, primaryResult);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music, fallbackResult);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Song", Type = MediaType.Audio };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — should be insufficient, fallback called and merged
        musicBrainz.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        Assert.NotNull(fetched);
        Assert.Equal("Band", fetched.Artist);
    }

    // ---- Album sufficiency tests ----

    [Fact]
    public async Task FetchMusicMetadata_AlbumSufficient_WhenHasTitleAndArt()
    {
        // Arrange — Album with title + poster URL (no artist needed for albums)
        var result = new MetadataResult { Title = "Album Name", PosterUrl = "http://example.com/cover.jpg" };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, result);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Album Name", Type = MediaType.Album };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — Album with title + art is sufficient
        Assert.NotNull(fetched);
        musicBrainz.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task FetchMusicMetadata_AlbumInsufficient_WhenMissingArt()
    {
        // Arrange — Album has title but no art
        var primaryResult = new MetadataResult { Title = "Album Name" };
        var fallbackResult = new MetadataResult { Title = "Album Name", PosterUrl = "http://example.com/cover.jpg" };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, primaryResult);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music, fallbackResult);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Album Name", Type = MediaType.Album };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — insufficient, fallback called
        musicBrainz.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        Assert.NotNull(fetched);
        Assert.Equal("http://example.com/cover.jpg", fetched.PosterUrl);
    }

    // ---- Artist sufficiency tests ----

    [Fact]
    public async Task FetchMusicMetadata_ArtistSufficient_WhenHasTitle()
    {
        // Arrange — Artist with just a title (no art required)
        var result = new MetadataResult { Title = "Artist Name" };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, result);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Artist Name", Type = MediaType.Artist };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — Artist only needs title to be sufficient
        Assert.NotNull(fetched);
        musicBrainz.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    // ---- Merge and dispatch tests ----

    [Fact]
    public async Task FetchMusicMetadata_FallbackMergesGaps()
    {
        // Arrange — Primary has title, fallback has poster
        var primaryResult = new MetadataResult { Title = "Song" };
        var fallbackResult = new MetadataResult { PosterUrl = "http://example.com/art.jpg", Year = 2023 };
        var embedded = CreateMockProvider("Embedded", LibraryType.Music, primaryResult);
        var musicBrainz = CreateMockProvider("MusicBrainz", LibraryType.Music, fallbackResult);
        var router = CreateRouter(embedded.Object, musicBrainz.Object);

        // Audio type — will be insufficient (missing artist + art) → triggers fallback
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Song", Type = MediaType.Audio };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert — merged result has primary title + fallback poster/year
        Assert.NotNull(fetched);
        Assert.Equal("Song", fetched.Title);
        Assert.Equal("http://example.com/art.jpg", fetched.PosterUrl);
        Assert.Equal(2023, fetched.Year);
    }

    [Fact]
    public async Task FetchMetadata_RoutesToCorrectProviderByType()
    {
        // Arrange
        var movieResult = new MetadataResult { Title = "Movie" };
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Movie, movieResult);
        var tvMaze = CreateMockProvider("TVMaze", LibraryType.TV);

        _mockSettings.Setup(s => s.GetSettingAsync("MovieProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(wikidata.Object, tvMaze.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Movie", Type = MediaType.Movie };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Movie);

        // Assert — should route to Wikidata, not TVMaze
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        tvMaze.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task FetchMetadata_HandlesKeyedProvider()
    {
        // Arrange — Keyed provider should use FetchMetadataWithKeyAsync, not FetchMetadataAsync
        var keyedMock = new Mock<IKeyedMetadataProvider>();
        keyedMock.Setup(p => p.ProviderName).Returns("OMDb");
        keyedMock.Setup(p => p.SupportedType).Returns(LibraryType.Movie);
        keyedMock.Setup(p => p.GetActiveApiKey(It.IsAny<string>(), It.IsAny<string>())).Returns("test-key");
        keyedMock.Setup(p => p.FetchMetadataWithKeyAsync(It.IsAny<MediaItem>(), "test-key", "softmedia"))
            .ReturnsAsync(new MetadataResult { Title = "Movie" });

        _mockSettings.Setup(s => s.GetSettingAsync("MovieProvider", "Wikidata")).ReturnsAsync("OMDb");
        _mockSettings.Setup(s => s.GetSettingAsync("OMDbApiKeyMode", "softmedia")).ReturnsAsync("softmedia");
        _mockSettings.Setup(s => s.GetSettingAsync("OMDbApiKeyCustom", "")).ReturnsAsync("");

        var router = CreateRouter(keyedMock.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Movie", Type = MediaType.Movie };

        // Act
        var fetched = await router.FetchMetadataAsync(item, LibraryType.Movie);

        // Assert — should use keyed path, never direct FetchMetadataAsync
        keyedMock.Verify(p => p.FetchMetadataWithKeyAsync(It.IsAny<MediaItem>(), "test-key", "softmedia"), Times.Once);
        keyedMock.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }
}
