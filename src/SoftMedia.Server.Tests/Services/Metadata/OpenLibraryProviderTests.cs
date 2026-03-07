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
}
