using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
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
        Assert.NotNull(result!.Cast);
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
        Assert.NotNull(result.Cast);
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
        Assert.NotNull(result.Cast);
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

    [Fact]
    public async Task FetchMetadataAsync_MultiAuthor_KeepsEntriesAttributedToAnyAuthor()
    {
        // Hunters of Dune failure mode: EPUB embedded author reads
        // "Brian Herbert and Kevin J. Anderson" → old scorer extracted only the
        // LAST surname ("Anderson"), filtering out every OL entry attributed
        // solely to Brian Herbert. The multi-surname extractor must keep those.
        var item = new MediaItem
        {
            Title = "Hunters of Dune",
            Type = MediaType.Book,
            Director = "Brian Herbert and Kevin J. Anderson",
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                // Herbert-only attribution — MUST survive the surname filter.
                new { title = "Hunters of Dune", first_publish_year = 2006,
                      cover_i = (int?)8415666, author_name = new[] { "Brian Herbert" },
                      edition_count = 3 },
                // Anderson-only attribution — also passes on surname match.
                new { title = "Hunters of Dune", first_publish_year = 2006,
                      cover_i = (int?)99, author_name = new[] { "Kevin J. Anderson" },
                      edition_count = 1 },
                // Unrelated Eloff entry — filtered out (neither surname present).
                new { title = "Hunters of the Dunes", first_publish_year = 2016,
                      cover_i = (int?)11981600, author_name = new[] { "Fritz Eloff" },
                      edition_count = 1 },
            }
        };

        var provider = BuildProvider(mockResponse, out _);
        var result = await provider.FetchMetadataAsync(item);

        // Top scorer should be the Brian Herbert edition (more editions ties go
        // to it, and title/author match exactly). The Eloff entry would have
        // been silently selected under the old last-surname-only filter because
        // the Herbert entries would have been dropped.
        Assert.NotNull(result);
        Assert.Equal("https://covers.openlibrary.org/b/id/8415666-L.jpg", result!.PosterUrl);
        Assert.NotNull(result.Cast);
        Assert.Contains(result.Cast, c => c.Name == "Brian Herbert");
    }

    [Fact]
    public async Task FetchMetadataAsync_StripsLeadingArticle_FromOutboundQuery()
    {
        // OpenLibrary's Solr `title=` index drops leading articles ("A", "An",
        // "The"). Querying `title=A+Face+In+The+Crowd` returns zero docs. The
        // article must be stripped before the URL is built so the query aligns
        // with the index.
        var item = new MediaItem
        {
            Title = "A Face in the Crowd",
            Type = MediaType.Book,
            Director = "Stephen King",
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                new { title = "Face in the Crowd", first_publish_year = 2012,
                      cover_i = (int?)14655702,
                      author_name = new[] { "Stephen King", "Stewart O'Nan" },
                      edition_count = 5 },
            }
        };

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
            });

        var provider = new OpenLibraryProvider(
            new HttpClient(handlerMock.Object),
            new Mock<ILogger<OpenLibraryProvider>>().Object,
            new SoftMedia.Server.Helpers.RateLimiterFactory());

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        // "A " is stripped, but the remaining title tokens must still be present.
        Assert.Contains("Face", url);
        Assert.Contains("Crowd", url);
        Assert.DoesNotContain("title=A%20", url);
        Assert.DoesNotContain("title=A+", url);

        Assert.NotNull(result);
        Assert.Equal("https://covers.openlibrary.org/b/id/14655702-L.jpg", result!.PosterUrl);
    }

    [Fact]
    public async Task FetchMetadataAsync_NormalizesPunctuation_InOutboundQueryTitle()
    {
        // EPUB embedded title "11/22/63: A Novel" previously went to OL with
        // slashes and colon URL-encoded, which the Solr title index mis-parses.
        // Normalisation strips non-alphanumerics to spaces before URL build.
        var item = new MediaItem
        {
            Title = "11/22/63: A Novel",
            Type = MediaType.Book,
            Director = "Stephen King",
        };

        var mockResponse = new
        {
            docs = new object[]
            {
                new { title = "11/22/63", first_publish_year = 2011,
                      cover_i = (int?)8675309,
                      author_name = new[] { "Stephen King" },
                      edition_count = 20 },
            }
        };

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(mockResponse)),
            });

        var provider = new OpenLibraryProvider(
            new HttpClient(handlerMock.Object),
            new Mock<ILogger<OpenLibraryProvider>>().Object,
            new SoftMedia.Server.Helpers.RateLimiterFactory());

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        // Slashes and colons must be gone from the outbound title param.
        Assert.DoesNotContain("%2F", url);      // URL-encoded slash
        Assert.DoesNotContain("%3A", url);      // URL-encoded colon
        // Digits must survive punctuation stripping.
        Assert.Contains("11", url);
        Assert.Contains("22", url);
        Assert.Contains("63", url);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_IsbnFirst_ShortCircuitsSearch_WhenExtractorReturnsIsbn()
    {
        // When the book file has a publisher-stamped ISBN, the provider must
        // query `search.json?isbn=...` before doing any title/author search —
        // ISBN lookup is authoritative and avoids the scoring heuristics
        // entirely.
        var item = new MediaItem
        {
            Title = "Irrelevant Title",        // bad title must not matter
            Path = "C:/fake/book.epub",
            Type = MediaType.Book,
            Director = "Wrong Author",         // bad author must not matter
        };

        var extractor = new Mock<IBookMetadataExtractor>();
        extractor.Setup(e => e.ExtractAsync(item.Path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookFileMetadata(
                Title: null, Author: null, Year: null, Publisher: null,
                Description: null, Isbn: "9780812580273", Language: null));

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        var isbnResponse = new
        {
            docs = new object[]
            {
                new { title = "Real Title From ISBN", first_publish_year = 1999,
                      cover_i = (int?)555111,
                      author_name = new[] { "Real Author" },
                      edition_count = 10 }
            }
        };
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(isbnResponse)),
            });

        var provider = new OpenLibraryProvider(
            new HttpClient(handlerMock.Object),
            new Mock<ILogger<OpenLibraryProvider>>().Object,
            new SoftMedia.Server.Helpers.RateLimiterFactory(),
            extractor.Object);

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(captured);
        Assert.Contains("isbn=9780812580273", captured!.RequestUri!.ToString());
        Assert.NotNull(result);
        Assert.Equal("Real Title From ISBN", result!.Title);
        Assert.Equal("https://covers.openlibrary.org/b/id/555111-L.jpg", result.PosterUrl);
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
