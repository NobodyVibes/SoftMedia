using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// Wave D — verifies the Movie / TV primary+fallback chain. Mirrors the
/// fixture style used by MetadataRouterTests for music routing.
public class MetadataRouterMovieTvChainTests
{
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<ILogger<MetadataRouter>> _logger = new();

    public MetadataRouterMovieTvChainTests()
    {
        // Music defaults so the router constructor / unrelated branches stay quiet.
        _settings.Setup(s => s.GetSettingAsync("MusicProvider", "MusicBrainz"))
            .ReturnsAsync("MusicBrainz");
    }

    private MetadataRouter CreateRouter(params IMetadataProvider[] providers) =>
        new(providers, _settings.Object, _logger.Object);

    private static Mock<IMetadataProvider> Provider(string name, LibraryType type, MetadataResult? result = null)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.SupportedType).Returns(type);
        mock.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>())).ReturnsAsync(result);
        return mock;
    }

    private void StubMovieProviders(string primary, string fallback)
    {
        _settings.Setup(s => s.GetSettingAsync("MovieProvider", "Wikidata")).ReturnsAsync(primary);
        _settings.Setup(s => s.GetSettingAsync("MovieFallbackProvider", "Nfo")).ReturnsAsync(fallback);
    }

    private void StubTvProviders(string primary, string fallback)
    {
        _settings.Setup(s => s.GetSettingAsync("TVProvider", "TVMaze")).ReturnsAsync(primary);
        _settings.Setup(s => s.GetSettingAsync("TVFallbackProvider", "Nfo")).ReturnsAsync(fallback);
    }

    private static MediaItem MovieItem(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, SortTitle = title,
        Type = MediaType.Movie, Path = $"/lib/{title}.mkv",
    };

    [Fact]
    public async Task PrimarySufficient_DoesNotInvokeFallback()
    {
        StubMovieProviders("Wikidata", "Nfo");
        var primary = Provider("Wikidata", LibraryType.Movie,
            new MetadataResult { Title = "Inception", Description = "...", Year = 2010 });
        var fallback = Provider("Nfo", LibraryType.Movie,
            new MetadataResult { Title = "Should Not Be Used" });

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("Inception"), LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("Inception", result!.Title);
        // SM-WI-030(b): the NFO fallback is PRE-READ once (free local read, may seed an
        // IMDb id for the primary) — but a sufficient primary's result still wins and
        // the NFO is never parsed a second time.
        fallback.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.AtMostOnce);
    }

    [Fact]
    public async Task PrimaryReturnsNull_FallbackResultUsed()
    {
        StubMovieProviders("Wikidata", "Nfo");
        var primary = Provider("Wikidata", LibraryType.Movie, result: null);
        var fallback = Provider("Nfo", LibraryType.Movie,
            new MetadataResult { Title = "From NFO", Year = 2010 });

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("From NFO", result!.Title);
        fallback.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task PrimaryInsufficient_FallbackFillsGaps()
    {
        // Primary returns only Title — insufficient (needs description / poster /
        // year). Fallback fills in the gaps; primary's title still wins.
        StubMovieProviders("Wikidata", "Nfo");
        var primary = Provider("Wikidata", LibraryType.Movie,
            new MetadataResult { Title = "Primary Title" });
        var fallback = Provider("Nfo", LibraryType.Movie,
            new MetadataResult
            {
                Title = "Fallback Title",
                Description = "Plot from NFO",
                PosterUrl = "https://example.com/poster.jpg",
                Year = 1999,
            });

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("Primary Title", result!.Title); // primary wins
        Assert.Equal("Plot from NFO", result.Description);
        Assert.Equal("https://example.com/poster.jpg", result.PosterUrl);
        Assert.Equal(1999, result.Year);
    }

    [Fact]
    public async Task FallbackNone_DisablesFallbackEvenWhenPrimaryReturnsNull()
    {
        StubMovieProviders("Wikidata", "None");
        var primary = Provider("Wikidata", LibraryType.Movie, result: null);
        var fallback = Provider("Nfo", LibraryType.Movie,
            new MetadataResult { Title = "Should Not Be Used" });

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.Null(result);
        fallback.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task BothReturnNull_ReturnsNull()
    {
        StubMovieProviders("Wikidata", "Nfo");
        var primary = Provider("Wikidata", LibraryType.Movie, result: null);
        var fallback = Provider("Nfo", LibraryType.Movie, result: null);

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.Null(result);
    }

    [Fact]
    public async Task NfoAsPrimary_AndNoFallback_StillReturnsResult()
    {
        // Users who explicitly want NFO-first: MovieProvider=Nfo,
        // MovieFallbackProvider=None means just NFO. Should not be a no-op.
        StubMovieProviders("Nfo", "None");
        var nfo = Provider("Nfo", LibraryType.Movie,
            new MetadataResult { Title = "From NFO", Year = 2010 });

        var router = CreateRouter(nfo.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("From NFO", result!.Title);
    }

    [Fact]
    public async Task PrimaryAndFallbackSameProvider_FallbackNotDoubleInvoked()
    {
        // Misconfiguration safeguard — if MovieProvider=MovieFallbackProvider,
        // the chain must not call the same provider twice.
        StubMovieProviders("Wikidata", "Wikidata");
        var primary = Provider("Wikidata", LibraryType.Movie, result: null);

        var router = CreateRouter(primary.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.Null(result);
        primary.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task PrimaryThrows_FallbackStillRuns()
    {
        StubMovieProviders("Wikidata", "Nfo");
        var primary = new Mock<IMetadataProvider>();
        primary.Setup(p => p.ProviderName).Returns("Wikidata");
        primary.Setup(p => p.SupportedType).Returns(LibraryType.Movie);
        primary.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()))
            .ThrowsAsync(new HttpRequestException("network broken"));
        var fallback = Provider("Nfo", LibraryType.Movie,
            new MetadataResult { Title = "Fallback Saves The Day", Year = 2020 });

        var router = CreateRouter(primary.Object, fallback.Object);

        var result = await router.FetchMetadataAsync(MovieItem("X"), LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("Fallback Saves The Day", result!.Title);
    }

    // ---- TV chain ----

    [Fact]
    public async Task TvPrimarySufficient_DoesNotInvokeFallback()
    {
        StubTvProviders("TVMaze", "Nfo");
        var primary = Provider("TVMaze", LibraryType.TV,
            new MetadataResult { Title = "Show", Description = "...", Year = 2018 });
        var fallback = Provider("Nfo", LibraryType.TV,
            new MetadataResult { Title = "Should Not Be Used" });

        var router = CreateRouter(primary.Object, fallback.Object);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Series,
            Title = "Show", SortTitle = "Show", Path = "/tv/Show",
        };

        var result = await router.FetchMetadataAsync(item, LibraryType.TV);

        Assert.NotNull(result);
        Assert.Equal("Show", result!.Title);
        // SM-WI-030(b): NFO pre-read allowed (see movie variant) — primary still wins.
        fallback.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.AtMostOnce);
    }

    [Fact]
    public async Task TvPrimaryNull_FallbackUsed()
    {
        StubTvProviders("TVMaze", "Nfo");
        var primary = Provider("TVMaze", LibraryType.TV, result: null);
        var fallback = Provider("Nfo", LibraryType.TV,
            new MetadataResult { Title = "From NFO", Year = 2018 });

        var router = CreateRouter(primary.Object, fallback.Object);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Series,
            Title = "Show", SortTitle = "Show", Path = "/tv/Show",
        };

        var result = await router.FetchMetadataAsync(item, LibraryType.TV);

        Assert.NotNull(result);
        Assert.Equal("From NFO", result!.Title);
    }
}
