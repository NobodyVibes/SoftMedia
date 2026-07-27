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
    private readonly Mock<ITvMetadataEnricher> _mockTvEnricher;
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
        _mockTvEnricher = new Mock<ITvMetadataEnricher>();
        _mockLogger = new Mock<ILogger<MetadataAggregator>>();

        // Default: ExtractAndQueueAsync returns true (images found)
        _mockImageExtractor
            .Setup(x => x.ExtractAndQueueAsync(It.IsAny<MediaItem>(), It.IsAny<MetadataResult>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private MetadataAggregator CreateAggregator()
    {
        // Wave E2 — collection enrichment is a no-op in these tests.
        var collectionEnrichment = new Mock<SoftMedia.Server.Services.Metadata.Collections.ICollectionEnrichmentService>();
        collectionEnrichment.Setup(s => s.EnrichMovieCollectionAsync(It.IsAny<MediaItem>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new MetadataAggregator(
            new[] { _mockProvider.Object },
            _mockRouter.Object,
            _mockSettings.Object,
            _mockImageExtractor.Object,
            _mockTvEnricher.Object,
            collectionEnrichment.Object,
            _dbContext,
            Moq.Mock.Of<SoftMedia.Server.Services.Abstractions.IImageCacheService>(),
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

        var result = new MetadataResult
        {
            Title = "Test Movie",
            PosterUrl = "http://example.com/poster.jpg",
            Year = 2023
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        // Assert
        // 1. Verify ImageUrlExtractorService was called to extract and queue images
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.Is<MediaItem>(m => m.Id == item.Id),
            It.IsAny<MetadataResult>()), Times.Once);

        // 2. Verify metadata was saved to DB
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.NotNull(savedItem!.PosterUrl);
        Assert.Equal(2023, savedItem.Year);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPopulateExternalIds()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Show", Type = MediaType.Series };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "Test Show",
            ImdbId = "tt1234567",
            TvMazeId = 999,
            MusicBrainzId = "mb-id-123"
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.TV))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.TV);

        // Assert
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("tt1234567", savedItem!.ImdbId);
        Assert.Equal(999, savedItem.TvMazeId);
        Assert.Equal("mb-id-123", savedItem.MusicBrainzId);
    }

    // ──────────────────────────────────────────────────────── book metadata

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPersistBookPublisherIsbnAndPageCount()
    {
        // These three used to be read off MetadataResult by nobody: there were no columns to
        // put them in, so OpenLibrary's answer was fetched and then dropped, which is why
        // every book showed Publisher/ISBN/Pages as blank.
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "The Shining", Type = MediaType.Book };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Book))
            .ReturnsAsync(new MetadataResult
            {
                Title = "The Shining",
                Publisher = "Doubleday",
                Isbn = "978-0-385-12167-5",
                PageCount = 447
            });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Book);

        var saved = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("Doubleday", saved!.Studio);
        Assert.Equal("9780385121675", saved.Isbn);   // normalised on the way in
        Assert.Equal(447, saved.PageCount);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldNotOverwriteFileSourcedIsbnAndPageCount()
    {
        // The scanner already read these out of the file itself, which describes the exact
        // edition on disk. A provider result describes the work and may be a different
        // printing, so it must fill gaps only — never displace a PDF's real page count.
        var aggregator = CreateAggregator();
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "The Shining",
            Type = MediaType.Book,
            Isbn = "0385121679",
            PageCount = 512
        };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Book))
            .ReturnsAsync(new MetadataResult
            {
                Title = "The Shining",
                Isbn = "9780385121675",
                PageCount = 447
            });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Book);

        var saved = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("0385121679", saved!.Isbn);
        Assert.Equal(512, saved.PageCount);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPreferExplicitStudioOverPublisher()
    {
        // Studio is the shared producing-organisation column; Publisher is the book-flavoured
        // alias that only fills it when nothing better arrived.
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Book", Type = MediaType.Book };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Book))
            .ReturnsAsync(new MetadataResult { Studio = "Scribner", Publisher = "Fallback Press" });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Book);

        var saved = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("Scribner", saved!.Studio);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldRejectJunkIsbnFromProvider()
    {
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Book", Type = MediaType.Book };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Book))
            .ReturnsAsync(new MetadataResult { Isbn = "OL12345W", PageCount = 0 });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Book);

        var saved = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Null(saved!.Isbn);
        Assert.Null(saved.PageCount);   // a zero page count is worse than no page count
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

        var result = new MetadataResult
        {
            Title = "Test Series",
            Seasons = new List<SeasonMetadata>
            {
                new SeasonMetadata { Number = 1, PosterUrl = "http://example.com/s1.jpg" },
                new SeasonMetadata { Number = 2, PosterUrl = "http://example.com/s2.jpg" }
            }
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.TV))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.TV);

        // Assert
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.Is<MediaItem>(m => m.Id == item.Id && m.Type == MediaType.Series),
            It.IsAny<MetadataResult>()), Times.Once);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldSkipImages_WhenDeferImageCachingIsTrue()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Deferred Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "Deferred Movie",
            PosterUrl = "http://example.com/poster.jpg"
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie, deferImageCaching: true);

        // Assert — Image extractor should NOT be called when deferred
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.IsAny<MediaItem>(),
            It.IsAny<MetadataResult>()), Times.Never);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldPromotePosterUrl()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Poster Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "Poster Movie",
            PosterUrl = "http://example.com/promoted-poster.jpg"
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        // Assert — PosterUrl should be promoted to the column before image extraction strips it
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("http://example.com/promoted-poster.jpg", savedItem!.PosterUrl);
    }

    [Fact]
    public async Task EnrichMediaItemAsync_ShouldNotOverwritePosterUrl_WhenNull()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "No Poster Movie",
            Type = MediaType.Movie,
            PosterUrl = "http://example.com/existing-poster.jpg"
        };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "No Poster Movie",
            PosterUrl = null // Provider returned no poster
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        // Assert — existing PosterUrl should be preserved
        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal("http://example.com/existing-poster.jpg", savedItem!.PosterUrl);
    }

    /// <summary>
    /// Lost-update regression: ImageDownloadQueueService caches the poster and writes
    /// "/cache/images/…" back on its OWN DbContext, often within milliseconds of the enqueue.
    /// If this context still had PosterUrl marked modified, MetadataQueueService's
    /// post-enrichment SaveChanges would carry the stale provider URL and clobber that path —
    /// which is exactly why movie posters were re-fetched through /api/v1/image/proxy on every
    /// library view even though the file sat in wwwroot/cache/images/movies. The promotion must
    /// therefore be flushed BEFORE the extractor hands the URL to the queue.
    /// </summary>
    [Fact]
    public async Task EnrichMediaItemAsync_FlushesPromotedPosterUrl_BeforeQueueingImageDownloads()
    {
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Race Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        bool? posterStillModifiedAtEnqueue = null;
        _mockImageExtractor
            .Setup(x => x.ExtractAndQueueAsync(It.IsAny<MediaItem>(), It.IsAny<MetadataResult>()))
            .Callback(() => posterStillModifiedAtEnqueue =
                _dbContext.Entry(item).Property(m => m.PosterUrl).IsModified)
            .ReturnsAsync(true);

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(new MetadataResult
            {
                Title = "Race Movie",
                PosterUrl = "http://example.com/race-poster.jpg"
            });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        Assert.False(posterStillModifiedAtEnqueue,
            "PosterUrl must be persisted before the image queue is handed the URL, otherwise "
            + "the caller's later SaveChanges overwrites the queue's cached /cache/images path.");
        Assert.Equal("http://example.com/race-poster.jpg", item.PosterUrl);
    }

    /// <summary>
    /// Once the art is cached on disk the column owns the "/cache/images/…" path: re-stamping
    /// the provider URL on the next enrichment would flip the library back onto the image proxy
    /// (and re-download the identical bytes into cache/images/proxy) until the queue caught up.
    /// The download is still queued — the extractor reads MetadataResult, not this column — so a
    /// cache file that went missing still heals under the same key.
    /// </summary>
    [Fact]
    public async Task EnrichMediaItemAsync_KeepsCachedPosterPath_WhenProviderReturnsRemoteUrl()
    {
        var aggregator = CreateAggregator();
        var cachedPath = "/cache/images/movies/" + Guid.NewGuid() + "_poster.jpg";
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Cached Movie",
            Type = MediaType.Movie,
            PosterUrl = cachedPath
        };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(new MetadataResult
            {
                Title = "Cached Movie",
                PosterUrl = "http://example.com/provider-poster.jpg"
            });

        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);

        var savedItem = await _dbContext.MediaItems.FindAsync(item.Id);
        Assert.Equal(cachedPath, savedItem!.PosterUrl);
        _mockImageExtractor.Verify(x => x.ExtractAndQueueAsync(
            It.Is<MediaItem>(m => m.Id == item.Id),
            It.Is<MetadataResult>(r => r.PosterUrl == "http://example.com/provider-poster.jpg")),
            Times.Once);
    }

    [Fact]
    public async Task PersistGenresAsync_ShouldCreateNewGenres_InSingleBatch()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Genre Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "Genre Movie",
            Genres = new List<string> { "Action", "Sci-Fi", "Drama" },
            PosterUrl = "http://example.com/poster.jpg"
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);
        await _dbContext.SaveChangesAsync();

        // Assert — all 3 genres should exist
        var genres = await _dbContext.Genres.ToListAsync();
        Assert.Equal(3, genres.Count);

        var junctions = await _dbContext.MediaItemGenres
            .Where(mg => mg.MediaItemId == item.Id)
            .ToListAsync();
        Assert.Equal(3, junctions.Count);
    }

    [Fact]
    public async Task PersistGenresAsync_DiffBased_ShouldOnlyAddMissing()
    {
        // Arrange
        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Diff Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        // First enrichment: Action + Sci-Fi
        var result1 = new MetadataResult
        {
            Title = "Diff Movie",
            Genres = new List<string> { "Action", "Sci-Fi" },
            PosterUrl = "http://example.com/poster.jpg"
        };
        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie)).ReturnsAsync(result1);
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);
        await _dbContext.SaveChangesAsync();

        // Second enrichment: Action + Drama (Sci-Fi removed, Drama added)
        var result2 = new MetadataResult
        {
            Title = "Diff Movie",
            Genres = new List<string> { "Action", "Drama" },
            PosterUrl = "http://example.com/poster.jpg"
        };
        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie)).ReturnsAsync(result2);
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);
        await _dbContext.SaveChangesAsync();

        // Assert — should have Action and Drama, not Sci-Fi
        var junctions = await _dbContext.MediaItemGenres
            .Where(mg => mg.MediaItemId == item.Id)
            .Include(mg => mg.Genre)
            .ToListAsync();
        Assert.Equal(2, junctions.Count);
        Assert.Contains(junctions, j => j.Genre!.Name == "Action");
        Assert.Contains(junctions, j => j.Genre!.Name == "Drama");
        Assert.DoesNotContain(junctions, j => j.Genre!.Name == "Sci-Fi");
    }

    [Fact]
    public async Task PersistCastAsync_ShouldReuseExistingPerson()
    {
        // Arrange — pre-create a Person entity
        var existingPerson = new Person { Name = "Tom Hanks", ExternalId = 31 };
        _dbContext.Persons.Add(existingPerson);
        await _dbContext.SaveChangesAsync();

        var aggregator = CreateAggregator();
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Cast Movie", Type = MediaType.Movie };
        _dbContext.MediaItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var result = new MetadataResult
        {
            Title = "Cast Movie",
            Cast = new List<CastMember>
            {
                new CastMember { Id = 31, Name = "Tom Hanks", Character = "Forrest" }
            },
            PosterUrl = "http://example.com/poster.jpg"
        };

        _mockRouter.Setup(x => x.FetchMetadataAsync(item, LibraryType.Movie))
            .ReturnsAsync(result);

        // Act
        await aggregator.EnrichMediaItemAsync(item, LibraryType.Movie);
        await _dbContext.SaveChangesAsync();

        // Assert — should not create a duplicate Person
        var persons = await _dbContext.Persons.ToListAsync();
        Assert.Single(persons);
        Assert.Equal("Tom Hanks", persons[0].Name);

        var castEntry = await _dbContext.MediaItemCasts
            .FirstOrDefaultAsync(mc => mc.MediaItemId == item.Id);
        Assert.NotNull(castEntry);
        Assert.Equal(existingPerson.Id, castEntry!.PersonId);
        Assert.Equal("Forrest", castEntry.Character);
    }
}
