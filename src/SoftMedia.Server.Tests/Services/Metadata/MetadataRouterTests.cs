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

    // ---- Comic routing tests ----

    [Fact]
    public async Task FetchComicMetadata_RoutesToComicProvider_NotOpenLibrary()
    {
        var comicInfoResult = new MetadataResult { Title = "Amazing-Man Comics", Description = "Series summary", PosterUrl = "poster.jpg" };
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, comicInfoResult);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book);
        var openLibrary = CreateMockProvider("Open Library", LibraryType.Book);

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(comicInfo.Object, wikidata.Object, openLibrary.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Amazing-Man Comics", Type = MediaType.ComicSeries };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.NotNull(fetched);
        Assert.Equal("Amazing-Man Comics", fetched!.Title);
        comicInfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        openLibrary.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);  // must never hit OL for comics
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);     // primary sufficient → no fallback
    }

    [Fact]
    public async Task FetchBookMetadata_UnchangedPath_RoutesToOpenLibrary()
    {
        var openLibResult = new MetadataResult { Title = "Pride and Prejudice" };
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book);
        var openLibrary = CreateMockProvider("Open Library", LibraryType.Book, openLibResult);

        _mockSettings.Setup(s => s.GetSettingAsync("BookProvider", "Open Library")).ReturnsAsync("Open Library");

        var router = CreateRouter(comicInfo.Object, wikidata.Object, openLibrary.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Pride and Prejudice", Type = MediaType.Book };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.Equal("Pride and Prejudice", fetched?.Title);
        openLibrary.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        comicInfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task FetchComicMetadata_InsufficientPrimary_FallsBackAndMerges()
    {
        // Primary returns only a title (no description/cover) → insufficient
        var primaryResult = new MetadataResult { Title = "Mystery Men Comics" };
        var fallbackResult = new MetadataResult { Description = "Series summary", PosterUrl = "poster.jpg", Year = 1940, Publisher = "Fox Features" };
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, primaryResult);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book, fallbackResult);

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(comicInfo.Object, wikidata.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Mystery Men Comics", Type = MediaType.ComicSeries };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.NotNull(fetched);
        Assert.Equal("Mystery Men Comics", fetched!.Title);        // primary wins
        Assert.Equal("Series summary", fetched.Description);       // fallback filled
        Assert.Equal("poster.jpg", fetched.PosterUrl);             // fallback filled
        Assert.Equal(1940, fetched.Year);                          // fallback filled
        Assert.Equal("Fox Features", fetched.Publisher);           // fallback filled
        comicInfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task FetchComicMetadata_FallbackNone_DisablesFallback()
    {
        var primaryResult = new MetadataResult { Title = "Weird Fantasy" }; // insufficient, no desc/cover
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, primaryResult);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book);

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("None");

        var router = CreateRouter(comicInfo.Object, wikidata.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Weird Fantasy", Type = MediaType.ComicSeries };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.NotNull(fetched);
        Assert.Equal("Weird Fantasy", fetched!.Title);
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never); // disabled
    }

    [Fact]
    public async Task FetchComicMetadata_PrimaryReturnsNull_RunsFallback()
    {
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, null); // no ComicInfo.xml available
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book, new MetadataResult { Title = "Series", Description = "From Wikidata", PosterUrl = "img" });

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(comicInfo.Object, wikidata.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Series", Type = MediaType.ComicSeries };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.NotNull(fetched);
        Assert.Equal("From Wikidata", fetched!.Description);
        wikidata.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task FetchComicMetadata_BothNull_ReturnsNull()
    {
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, null);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book, null);

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(comicInfo.Object, wikidata.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Unknown", Type = MediaType.ComicIssue };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task FetchComicMetadata_ComicIssueAlsoRouted()
    {
        // Same branch should handle ComicIssue items too.
        var result = new MetadataResult { Title = "Issue #5", Description = "Issue summary", PosterUrl = "cover.jpg" };
        var comicInfo = CreateMockProvider("ComicInfo", LibraryType.Book, result);
        var wikidata = CreateMockProvider("Wikidata", LibraryType.Book);
        var openLibrary = CreateMockProvider("Open Library", LibraryType.Book);

        _mockSettings.Setup(s => s.GetSettingAsync("ComicProvider", "ComicInfo")).ReturnsAsync("ComicInfo");
        _mockSettings.Setup(s => s.GetSettingAsync("ComicFallbackProvider", "Wikidata")).ReturnsAsync("Wikidata");

        var router = CreateRouter(comicInfo.Object, wikidata.Object, openLibrary.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Issue #5", Type = MediaType.ComicIssue };

        var fetched = await router.FetchMetadataAsync(item, LibraryType.Book);

        Assert.Equal("Issue #5", fetched?.Title);
        comicInfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
        openLibrary.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
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
