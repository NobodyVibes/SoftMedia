using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Services.Metadata;
using System.Net;
using Xunit;

namespace SoftMedia.Tests.Services.Metadata;

public class MetadataProviderTests
{
    [Fact]
    public async Task WikidataProvider_FetchesData()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"search\": []}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new WikidataProvider(httpClient, Mock.Of<ILogger<WikidataProvider>>());

        // Act
        var result = await provider.FetchMetadataAsync("Matrix");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("search", result);
    }

    [Fact]
    public async Task TVMazeProvider_FetchesData()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"name\": \"Friends\"}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new TVMazeProvider(httpClient, Mock.Of<ILogger<TVMazeProvider>>());

        // Act
        var result = await provider.FetchMetadataAsync("Friends");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Friends", result);
    }
}
