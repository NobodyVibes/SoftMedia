using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class OpenLibraryProviderTests
{
    [Fact]
    public async Task FetchMetadataAsync_ScoresAndSelectsBestMatch()
    {
        // Arrange
        var title = "The Lord of the Rings";
        var item = new MediaItem { Title = title, Year = 1954, Type = MediaType.Book };

        var mockResponse = new
        {
            docs = new[]
            {
                new { title = "Lord of the Rings (Wrong Year)", first_publish_year = 2000, cover_i = (int?)null, author_name = new[] { "Author A" } },
                new { title = "The Lord of the Rings", first_publish_year = 1954, cover_i = (int?)123456, author_name = new[] { "J.R.R. Tolkien" } }, // Best Match
                new { title = "The Lord of the Rings", first_publish_year = 1954, cover_i = (int?)null, author_name = new[] { "J.R.R. Tolkien" } } // No Cover, penalty
            }
        };

        var handlerMock = new Mock<HttpMessageHandler>();
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
        };

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handlerMock.Object);
        var loggerMock = new Mock<ILogger<OpenLibraryProvider>>();
        var rateLimiterFactory = new SoftMedia.Server.Helpers.RateLimiterFactory();

        var provider = new OpenLibraryProvider(httpClient, loggerMock.Object, rateLimiterFactory);

        // Act
        var result = await provider.FetchMetadataAsync(item);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("The Lord of the Rings", result.Title);
        Assert.Equal(1954, result.Year);
        Assert.Single(result.Cast);
        Assert.Equal("J.R.R. Tolkien", result.Cast.First().Name);
        Assert.Equal("https://covers.openlibrary.org/b/id/123456-L.jpg", result.PosterUrl);
    }

    [Fact]
    public async Task FetchMetadataAsync_UsesAuthorParam_WhenAuthorIsSetInDirector()
    {
        // Arrange
        var item = new MediaItem
        {
            Title = "Dune",
            Year = 1965,
            Type = MediaType.Book,
            Director = "Frank Herbert"
        };

        var mockResponse = new { docs = new[] { new { title = "Dune", first_publish_year = 1965, cover_i = (int?)1, author_name = new[] { "Frank Herbert" } } } };

        var handlerMock = new Mock<HttpMessageHandler>();
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
        };

        HttpRequestMessage? capturedRequest = null;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handlerMock.Object);
        var loggerMock = new Mock<ILogger<OpenLibraryProvider>>();
        var rateLimiterFactory = new SoftMedia.Server.Helpers.RateLimiterFactory();
        var provider = new OpenLibraryProvider(httpClient, loggerMock.Object, rateLimiterFactory);

        // Act
        await provider.FetchMetadataAsync(item);

        // Assert — should use structured title= and author= params
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("title=", requestUrl);
        Assert.Contains("author=Frank", requestUrl);
        Assert.DoesNotContain("q=", requestUrl);
    }

    [Fact]
    public async Task FetchMetadataAsync_AuthorFilter_RejectsWrongAuthorEntries()
    {
        // The "Dune: House Atreides" failure mode: OpenLibrary returns an orphan
        // entry (coverless, authorless) before the real Herbert & Anderson one.
        // With the author filter + sibling filter, the orphan must be dropped
        // and the canonical entry must win.
        var item = new MediaItem
        {
            Title = "Dune: House Atreides",
            Type = MediaType.Book,
            Director = "Brian Herbert"
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                // Orphan stub — title matches exactly but no author, no cover.
                new { title = "Dune: House Atreides", first_publish_year = (int?)null,
                      cover_i = (int?)null, author_name = (string[]?)null, edition_count = 1 },
                // Canonical entry — matches author surname, has cover, many editions.
                new { title = "Dune: House Atreides", first_publish_year = (int?)1999,
                      cover_i = (int?)987654,
                      author_name = new[] { "Brian Herbert", "Kevin J. Anderson" },
                      edition_count = 42 },
            }
        };

        var provider = BuildProvider(mockResponse, out _);
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal(1999, result!.Year);
        Assert.Contains(result.Cast, c => c.Name == "Brian Herbert");
        Assert.Equal("https://covers.openlibrary.org/b/id/987654-L.jpg", result.PosterUrl);
    }

    [Fact]
    public async Task FetchMetadataAsync_PoorMatch_ReturnsNull()
    {
        // When nothing in the result set scores well (title wildly different,
        // author wrong), we'd rather return null than stamp the wrong book. The
        // retry loop will re-query later; in the meantime the detail page stays
        // clean instead of showing a confident-but-incorrect cover.
        var item = new MediaItem
        {
            Title = "Some Obscure Golden Age Novella",
            Type = MediaType.Book,
            Director = "Nobody Famous"
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                // A completely different book that happened to surface for the query.
                new { title = "An Entirely Different Novel", first_publish_year = 1892,
                      cover_i = (int?)111, author_name = new[] { "Someone Else" },
                      edition_count = 3 },
            }
        };

        var provider = BuildProvider(mockResponse, out _);
        var result = await provider.FetchMetadataAsync(item);

        // Title Levenshtein alone blows past the 100-point threshold; the author
        // mismatch adds another 200. Result must be null, not a wrong match.
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_TokenMatch_AcceptsMungedOpenLibraryTitles()
    {
        // Real-world: an OpenLibrary query for "Dune: House Atreides" returns the
        // correct book as `"House Atreides (Dune"` (unclosed paren, reversed order).
        // A character-level distance metric scored that as a ~150-point miss and
        // rejected a valid match. Token-based scoring must accept it because all
        // the meaningful words are present.
        var item = new MediaItem
        {
            Title = "Dune: House Atreides",
            Type = MediaType.Book,
            Director = "Brian Herbert"
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                new
                {
                    title = "House Atreides (Dune",
                    first_publish_year = 1999,
                    cover_i = (int?)371720,
                    author_name = new[] { "Brian Herbert", "Kevin J. Anderson" },
                    edition_count = 1,
                }
            }
        };

        var provider = BuildProvider(mockResponse, out _);
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("https://covers.openlibrary.org/b/id/371720-L.jpg", result!.PosterUrl);
        Assert.Contains(result.Cast, c => c.Name == "Brian Herbert");
    }

    [Fact]
    public async Task FetchMetadataAsync_EditionCount_BreaksExactMatchTies()
    {
        // Two results identical on every scored dimension except edition_count.
        // The scorer's tiebreaker should pick the more-published work.
        var item = new MediaItem { Title = "Dune", Year = 1965, Type = MediaType.Book,
                                   Director = "Frank Herbert" };

        var mockResponse = new
        {
            docs = new object[]
            {
                new { title = "Dune", first_publish_year = 1965, cover_i = (int?)1,
                      author_name = new[] { "Frank Herbert" }, edition_count = 3 },
                new { title = "Dune", first_publish_year = 1965, cover_i = (int?)2,
                      author_name = new[] { "Frank Herbert" }, edition_count = 87 },
            }
        };

        var provider = BuildProvider(mockResponse, out _);
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("https://covers.openlibrary.org/b/id/2-L.jpg", result!.PosterUrl);
    }

    // Shared mock-handler builder — keeps per-test setup short.
    private static OpenLibraryProvider BuildProvider(object mockResponse, out HttpRequestMessage? captured)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        HttpRequestMessage? capturedRef = null;
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRef = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
            });

        captured = null;
        var provider = new OpenLibraryProvider(
            new HttpClient(handlerMock.Object),
            new Mock<ILogger<OpenLibraryProvider>>().Object,
            new SoftMedia.Server.Helpers.RateLimiterFactory());
        return provider;
    }

    [Fact]
    public async Task FetchMetadataAsync_UsesGenericQuery_WhenNoAuthorIsSetInDirector()
    {
        // Arrange
        var item = new MediaItem
        {
            Title = "Dune",
            Year = 1965,
            Type = MediaType.Book,
            Director = null // No author context
        };

        var mockResponse = new { docs = new[] { new { title = "Dune", first_publish_year = 1965, cover_i = (int?)1, author_name = new[] { "Frank Herbert" } } } };

        var handlerMock = new Mock<HttpMessageHandler>();
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
        };

        HttpRequestMessage? capturedRequest = null;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handlerMock.Object);
        var loggerMock = new Mock<ILogger<OpenLibraryProvider>>();
        var rateLimiterFactory = new SoftMedia.Server.Helpers.RateLimiterFactory();
        var provider = new OpenLibraryProvider(httpClient, loggerMock.Object, rateLimiterFactory);

        // Act
        await provider.FetchMetadataAsync(item);

        // Assert — should use generic q= param
        Assert.NotNull(capturedRequest);
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        Assert.Contains("q=", requestUrl);
        Assert.DoesNotContain("title=", requestUrl);
        Assert.DoesNotContain("author=", requestUrl);
    }
}
