using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using System.Net;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class ComicWikidataProviderTests
{
    private const string SampleSparqlJson = @"{
        ""results"": {
            ""bindings"": [{
                ""itemLabel"":  { ""value"": ""Amazing-Man Comics"" },
                ""year"":       { ""value"": ""1939"" },
                ""publisher"":  { ""value"": ""Centaur Publications"" },
                ""genres"":     { ""value"": ""superhero comic, adventure comic"" },
                ""description"":{ ""value"": ""Golden Age superhero series."" },
                ""image"":      { ""value"": ""http://commons.wikimedia.org/amazingman.jpg"" }
            }]
        }
    }";

    private static (ComicWikidataProvider provider, Mock<HttpMessageHandler> handler) NewProvider(
        HttpStatusCode status = HttpStatusCode.OK,
        string content = SampleSparqlJson,
        Exception? throwInstead = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        var setup = handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        if (throwInstead is not null)
            setup.ThrowsAsync(throwInstead);
        else
            setup.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(content)
            });

        var httpClient = new HttpClient(handler.Object);
        var provider = new ComicWikidataProvider(
            httpClient,
            NullLogger<ComicWikidataProvider>.Instance,
            new RateLimiterFactory());
        return (provider, handler);
    }

    [Fact]
    public void SupportedType_IsBook()
    {
        var (provider, _) = NewProvider();
        Assert.Equal(LibraryType.Book, provider.SupportedType);
        Assert.Equal("Wikidata", provider.ProviderName);
    }

    [Theory]
    [InlineData(MediaType.Book)]
    [InlineData(MediaType.Movie)]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.ComicIssue)] // Wikidata has no per-issue data
    public async Task FetchMetadataAsync_NonSeriesType_ShortCircuitsBeforeHttp(MediaType type)
    {
        var (provider, handler) = NewProvider();
        var item = new MediaItem { Type = type, Title = "Irrelevant" };

        var result = await provider.FetchMetadataAsync(item);

        Assert.Null(result);
        handler.Protected().Verify(
            "SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicSeries_ParsesSparqlResult()
    {
        var (provider, _) = NewProvider();
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Amazing-Man Comics" };

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal(1939, result!.Year);
        Assert.Equal("Centaur Publications", result.Publisher);
        Assert.Equal("Centaur Publications", result.Studio);
        Assert.Contains("Golden Age", result.Description);
        Assert.Equal("http://commons.wikimedia.org/amazingman.jpg", result.PosterUrl);
        Assert.NotNull(result.Genres);
        Assert.Contains("superhero comic", result.Genres!);
        Assert.Contains("adventure comic", result.Genres);
    }

    [Fact]
    public async Task FetchMetadataAsync_HitsWikidataSparqlEndpoint()
    {
        var (provider, handler) = NewProvider();
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Amazing-Man Comics" };

        await provider.FetchMetadataAsync(item);

        handler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri != null
                && req.RequestUri.AbsoluteUri.StartsWith("https://query.wikidata.org/sparql")
                && req.RequestUri.AbsoluteUri.Contains("format=json")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task FetchMetadataAsync_EmptyBindings_ReturnsNull()
    {
        var (provider, _) = NewProvider(content: @"{ ""results"": { ""bindings"": [] } }");
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Unknown Series" };

        var result = await provider.FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_HttpError_ReturnsNullGracefully()
    {
        var (provider, _) = NewProvider(status: HttpStatusCode.TooManyRequests, content: "Rate limited");
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Anything" };

        var result = await provider.FetchMetadataAsync(item);

        // The base WikidataSparqlClient catches HttpRequestException / timeouts and returns null.
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_MalformedJson_ReturnsNullGracefully()
    {
        var (provider, _) = NewProvider(content: "this is not json");
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Anything" };

        var result = await provider.FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_TimeoutException_ReturnsNullGracefully()
    {
        var (provider, _) = NewProvider(throwInstead: new TaskCanceledException("WDQS 60s timeout"));
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Anything" };

        var result = await provider.FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Amazing Man Comics Issue 005",         "Amazing Man Comics")]
    [InlineData("Mystery Men Comics Issue 012",         "Mystery Men Comics")]
    [InlineData("The Spirit #7",                        "The Spirit")]
    [InlineData("Tales From the Crypt 22 (1951)",       "Tales From the Crypt")]
    [InlineData("Watchmen",                             "Watchmen")] // no issue marker → untouched
    public void ExtractSearchTitle_StripsIssueNoise(string input, string expected)
    {
        var item = new MediaItem { Title = input };
        Assert.Equal(expected, ComicWikidataProvider.ExtractSearchTitle(item));
    }

    [Fact]
    public async Task FetchMetadataAsync_SparqlQueryIsWellFormedForComicSeries()
    {
        string? capturedUri = null;

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri?.AbsoluteUri)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(@"{""results"": {""bindings"": []}}")
            });

        var httpClient = new HttpClient(handler.Object);
        var provider = new ComicWikidataProvider(
            httpClient,
            NullLogger<ComicWikidataProvider>.Instance,
            new RateLimiterFactory());
        var item = new MediaItem { Type = MediaType.ComicSeries, Title = "Watchmen" };

        await provider.FetchMetadataAsync(item);

        Assert.NotNull(capturedUri);
        var decoded = Uri.UnescapeDataString(capturedUri!);

        // Narrow query, scoped to comic book series (Q1004) to avoid matching unrelated
        // books/movies. LIMIT 1 keeps WDQS well under the 60s timeout.
        Assert.Contains("wd:Q1004", decoded);
        Assert.Contains("LIMIT 1", decoded);
        // Must request JSON (parseable) not XML.
        Assert.Contains("format=json", decoded);
        // The search title must appear — escaped into the SPARQL literal.
        Assert.Contains("Watchmen", decoded);
        // Must query expected Wikidata properties.
        Assert.Contains("P123", decoded);  // publisher
        Assert.Contains("P571", decoded);  // inception
        Assert.Contains("P18", decoded);   // image
    }
}
