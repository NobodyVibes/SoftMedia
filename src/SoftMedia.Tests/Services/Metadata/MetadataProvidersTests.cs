using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Services.Metadata;
using System.Diagnostics;
using System.Net;
using Xunit;

namespace SoftMedia.Tests.Services.Metadata;

public class MetadataProvidersTests
{
    [Fact]
    public async Task MusicBrainzProvider_EnforcesRateLimit()
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
                Content = new StringContent("{}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new MusicBrainzProvider(httpClient, Mock.Of<ILogger<MusicBrainzProvider>>());

        // Act
        var stopwatch = Stopwatch.StartNew();
        await provider.FetchMetadataAsync("Test 1", "");
        await provider.FetchMetadataAsync("Test 2", "");
        stopwatch.Stop();

        // Assert
        // The first request is instant, the second should wait ~1.1s
        // So total time should be at least 1s
        Assert.True(stopwatch.ElapsedMilliseconds > 1000, "MusicBrainzProvider did not enforce rate limit");
        
        // Verify User-Agent
        Assert.Equal("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)", httpClient.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task OpenLibraryProvider_EnforcesRateLimit()
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
                Content = new StringContent("{}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new OpenLibraryProvider(httpClient, Mock.Of<ILogger<OpenLibraryProvider>>());

        // Act
        var stopwatch = Stopwatch.StartNew();
        await provider.FetchMetadataAsync("Book 1", "");
        await provider.FetchMetadataAsync("Book 2", "");
        stopwatch.Stop();

        // Assert
        // The first request is instant, the second should wait ~3.5s
        // So total time should be at least 3s
        Assert.True(stopwatch.ElapsedMilliseconds > 3000, "OpenLibraryProvider did not enforce rate limit");

        // Verify User-Agent
        Assert.Equal("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)", httpClient.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task GameMetadataProvider_FetchesData()
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
                Content = new StringContent("{}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new GameMetadataProvider(httpClient, Mock.Of<ILogger<GameMetadataProvider>>());

        // Act
        await provider.FetchMetadataAsync("Zelda", "");

        // Assert
        // Verify User-Agent
        Assert.Equal("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)", httpClient.DefaultRequestHeaders.UserAgent.ToString());
    }
}
