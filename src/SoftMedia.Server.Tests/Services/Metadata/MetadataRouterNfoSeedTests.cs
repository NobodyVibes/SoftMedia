using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SM-WI-030(b) — when the NFO sidecar is the configured fallback and carries an IMDb
/// id, the router reads it BEFORE the primary provider and seeds item.ImdbId so the
/// primary's ID-direct path replaces title guessing. The pre-read is reused as fallback
/// data (the NFO is parsed at most once per routing pass).
/// </summary>
public class MetadataRouterNfoSeedTests
{
    private static Mock<IMetadataProvider> Provider(string name, LibraryType type, Func<MediaItem, MetadataResult?> fetch)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(name);
        mock.SetupGet(p => p.SupportedType).Returns(type);
        mock.Setup(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()))
            .ReturnsAsync((MediaItem i) => fetch(i));
        return mock;
    }

    private static MetadataRouter CreateRouter(params IMetadataProvider[] providers)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MovieProvider", "Wikidata")).ReturnsAsync("Wikidata");
        settings.Setup(s => s.GetSettingAsync("MovieFallbackProvider", "Nfo")).ReturnsAsync("Nfo");
        return new MetadataRouter(providers, settings.Object, new Mock<ILogger<MetadataRouter>>().Object);
    }

    [Fact]
    public async Task NfoImdbId_IsSeeded_BeforePrimaryRuns()
    {
        string? imdbIdSeenByPrimary = "unset";
        var primary = Provider("Wikidata", LibraryType.Movie, i =>
        {
            imdbIdSeenByPrimary = i.ImdbId;
            return new MetadataResult { Title = "Small Soldiers", Year = 1998, Description = "Toys go to war." };
        });
        var nfo = Provider("Nfo", LibraryType.Movie, _ => new MetadataResult { ImdbId = "tt0122718" });
        var router = CreateRouter(primary.Object, nfo.Object);

        var item = new MediaItem { Title = "small soldiers", Year = 1998, Type = MediaType.Movie };
        var result = await router.FetchMetadataAsync(item, LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("tt0122718", imdbIdSeenByPrimary); // seeded before the primary call
        Assert.Equal("tt0122718", item.ImdbId);
        nfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task NfoPreRead_IsReusedAsFallback_WhenPrimaryInsufficient()
    {
        var primary = Provider("Wikidata", LibraryType.Movie, _ => null); // no match
        var nfo = Provider("Nfo", LibraryType.Movie, _ => new MetadataResult
        {
            Title = "Small Soldiers",
            ImdbId = "tt0122718",
            Description = "From the sidecar.",
        });
        var router = CreateRouter(primary.Object, nfo.Object);

        var item = new MediaItem { Title = "small soldiers", Type = MediaType.Movie };
        var result = await router.FetchMetadataAsync(item, LibraryType.Movie);

        Assert.NotNull(result);
        Assert.Equal("From the sidecar.", result!.Description);
        // ONE parse total: pre-read reused, not re-fetched for the fallback merge.
        nfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Once);
    }

    [Fact]
    public async Task PromotedImdbId_SkipsTheNfoPreRead()
    {
        var primary = Provider("Wikidata", LibraryType.Movie,
            _ => new MetadataResult { Title = "X", Description = "d", Year = 2000 });
        var nfo = Provider("Nfo", LibraryType.Movie, _ => new MetadataResult { ImdbId = "tt999" });
        var router = CreateRouter(primary.Object, nfo.Object);

        var item = new MediaItem { Title = "X", ImdbId = "tt0000001", Type = MediaType.Movie };
        await router.FetchMetadataAsync(item, LibraryType.Movie);

        Assert.Equal("tt0000001", item.ImdbId); // untouched
        nfo.Verify(p => p.FetchMetadataAsync(It.IsAny<MediaItem>()), Times.Never);
    }
}
