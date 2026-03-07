using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Tests;

public class WikidataProviderTests
{
    [Fact]
    public async Task FetchMetadataAsync_ShouldReturnMetadata_WhenTitleExists()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<WikidataProvider>>();
        var httpClient = new HttpClient(); // Real HTTP client for integration test
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SoftMediaTest/1.0 (test@softmedia.local)");
        // In a real CI/CD, we should mock this. But for this specific debugging task, 
        // we want to verify the SPARQL query against the real endpoint.
        
        var provider = new WikidataProvider(httpClient, mockLogger.Object, new RateLimiterFactory());
        var title = "Austin Powers: International Man of Mystery";

        // Act
        var result = await provider.FetchMetadataAsync(new MediaItem { Title = title });

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.PosterUrl);
        Assert.NotNull(result.Year);
        Assert.Equal(1997, result.Year);
    }

    [Fact]
    public async Task FetchMetadataAsync_ShouldParseResponseCorrectly()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<WikidataProvider>>();
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        
        var jsonResponse = @"{
            ""head"": { ""vars"": [ ""item"", ""itemLabel"", ""year"", ""poster"" ] },
            ""results"": {
                ""bindings"": [
                    {
                        ""item"": { ""type"": ""uri"", ""value"": ""http://www.wikidata.org/entity/Q123"" },
                        ""year"": { ""type"": ""literal"", ""value"": ""1999"" },
                        ""poster"": { ""type"": ""uri"", ""value"": ""http://example.com/poster.jpg"" }
                    }
                ]
            }
        }";

        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object);
        var provider = new WikidataProvider(httpClient, mockLogger.Object, new RateLimiterFactory());

        // Act
        var result = await provider.FetchMetadataAsync(new MediaItem { Title = "Test Movie" });

        // Assert
        Assert.NotNull(result);
        Assert.Equal("http://example.com/poster.jpg", result.PosterUrl);
        Assert.Equal(1999, result.Year);
    }
}
