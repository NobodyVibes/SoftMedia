using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata.Collections;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata.Collections;

/// Wave E2 — collection enrichment behavioural coverage.
public class CollectionEnrichmentServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<WikidataCollectionResolver> _resolver;
    private readonly Mock<ISettingsService> _settings;

    public CollectionEnrichmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"collenrich-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _resolver = new Mock<WikidataCollectionResolver>(
            new HttpClient(),
            NullLogger<WikidataCollectionResolver>.Instance,
            new SoftMedia.Server.Helpers.RateLimiterFactory());

        _settings = new Mock<ISettingsService>();
        _settings.Setup(s => s.GetSettingAsync("EnableWikidataCollectionLookup", "true"))
            .ReturnsAsync("true");
    }

    public void Dispose() => _db.Dispose();

    private CollectionEnrichmentService NewService() =>
        new(_db, _resolver.Object, _settings.Object, NullLogger<CollectionEnrichmentService>.Instance);

    private MediaItem MovieFixture(string imdbId, bool? attempted = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaType.Movie,
        Title = "Test Movie",
        SortTitle = "Test Movie",
        Path = "/lib/test.mkv",
        LibraryId = Guid.NewGuid(),
        ImdbId = imdbId,
        CollectionLookupAttempted = attempted,
    };

    [Fact]
    public async Task NonMovie_DoesNothing()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Series,
            Title = "Show", SortTitle = "Show", Path = "/tv/show",
            LibraryId = Guid.NewGuid(), ImdbId = "tt1234567",
        };

        await NewService().EnrichMovieCollectionAsync(item);

        Assert.Null(item.CollectionId);
        Assert.Null(item.CollectionLookupAttempted);
        _resolver.Verify(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SettingDisabled_DoesNothing()
    {
        _settings.Setup(s => s.GetSettingAsync("EnableWikidataCollectionLookup", "true"))
            .ReturnsAsync("false");

        var item = MovieFixture("tt1234567");
        await NewService().EnrichMovieCollectionAsync(item);

        Assert.Null(item.CollectionLookupAttempted);
        _resolver.Verify(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoImdbId_DoesNothing()
    {
        var item = MovieFixture(imdbId: "");
        await NewService().EnrichMovieCollectionAsync(item);

        Assert.Null(item.CollectionLookupAttempted);
        _resolver.Verify(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyAttempted_DoesNotReQuery()
    {
        // Sentinel: false means "we already looked up and there is no series".
        var item = MovieFixture("tt1234567", attempted: false);

        await NewService().EnrichMovieCollectionAsync(item);

        Assert.False(item.CollectionLookupAttempted);
        _resolver.Verify(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualCollectionAlreadyAttached_DoesNotOverwrite()
    {
        var manual = new Collection { Id = Guid.NewGuid(), Name = "My Curated Set", WikidataId = null };
        _db.Collections.Add(manual);
        await _db.SaveChangesAsync();

        var item = MovieFixture("tt1234567");
        item.CollectionId = manual.Id;
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        await NewService().EnrichMovieCollectionAsync(item);

        Assert.Equal(manual.Id, item.CollectionId);
        Assert.True(item.CollectionLookupAttempted);
        _resolver.Verify(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolverReturnsNull_MarksAttemptedFalse()
    {
        _resolver
            .Setup(r => r.ResolveByImdbIdAsync("tt1234567", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CollectionLookupResult?)null);

        var item = MovieFixture("tt1234567");
        await NewService().EnrichMovieCollectionAsync(item);

        Assert.Null(item.CollectionId);
        Assert.False(item.CollectionLookupAttempted);
    }

    [Fact]
    public async Task ResolverReturnsResult_CreatesCollectionAndAttaches()
    {
        _resolver
            .Setup(r => r.ResolveByImdbIdAsync("tt0120737", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionLookupResult("Q170461", "The Lord of the Rings", "https://example.com/lotr.jpg"));

        var item = MovieFixture("tt0120737");
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        await NewService().EnrichMovieCollectionAsync(item);
        await _db.SaveChangesAsync();

        Assert.True(item.CollectionLookupAttempted);
        Assert.NotNull(item.CollectionId);

        var collection = await _db.Collections.FirstAsync(c => c.WikidataId == "Q170461");
        Assert.Equal(collection.Id, item.CollectionId);
        Assert.Equal("The Lord of the Rings", collection.Name);
        Assert.Equal("https://example.com/lotr.jpg", collection.PosterUrl);
    }

    [Fact]
    public async Task SecondMovieInSameSeries_AttachesToExistingCollection()
    {
        // First movie creates the collection; the second resolves to the same
        // QID and must reuse it (uniqueness on WikidataId enforces this).
        _resolver
            .Setup(r => r.ResolveByImdbIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionLookupResult("Q170461", "The Lord of the Rings", null));

        var first = MovieFixture("tt0120737");
        _db.MediaItems.Add(first);
        await _db.SaveChangesAsync();
        await NewService().EnrichMovieCollectionAsync(first);
        await _db.SaveChangesAsync();

        var firstCollectionId = first.CollectionId;

        var second = MovieFixture("tt0167261");
        _db.MediaItems.Add(second);
        await _db.SaveChangesAsync();
        await NewService().EnrichMovieCollectionAsync(second);
        await _db.SaveChangesAsync();

        Assert.Equal(firstCollectionId, second.CollectionId);
        Assert.Equal(1, await _db.Collections.CountAsync(c => c.WikidataId == "Q170461"));
    }

    [Fact]
    public async Task ExistingAutoCollection_NameUpdatesOnRefresh()
    {
        // If the upstream Wikidata label has changed between scans, the
        // canonical name in the local DB updates accordingly.
        _resolver
            .Setup(r => r.ResolveByImdbIdAsync("tt0120737", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionLookupResult("Q170461", "Renamed LOTR", null));

        _db.Collections.Add(new Collection { Id = Guid.NewGuid(), Name = "Old Name", WikidataId = "Q170461" });
        await _db.SaveChangesAsync();

        var item = MovieFixture("tt0120737");
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        await NewService().EnrichMovieCollectionAsync(item);
        await _db.SaveChangesAsync();

        var refreshed = await _db.Collections.FirstAsync(c => c.WikidataId == "Q170461");
        Assert.Equal("Renamed LOTR", refreshed.Name);
    }
}
