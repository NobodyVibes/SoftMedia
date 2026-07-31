using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class OMDbProviderTests
{
    /// <summary>Canned-response handler that records every request URL.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> Requests { get; } = new();

        public StubHandler Enqueue(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        {
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body) });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent("""{"Response":"False","Error":"Movie not found!"}""") });
        }
    }

    private static (OMDbProvider Provider, StubHandler Handler, Mock<IOmdbUsageTracker> Tracker, Mock<INotificationService> Notifications)
        CreateProvider(StubHandler? handler = null, bool trackerAllows = true)
    {
        handler ??= new StubHandler();
        var tracker = new Mock<IOmdbUsageTracker>();
        tracker.Setup(t => t.TryRecordRequestAsync(It.IsAny<int>())).ReturnsAsync(trackerAllows);
        tracker.Setup(t => t.MarkExhaustedAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var notifications = new Mock<INotificationService>();
        notifications.Setup(n => n.HasActiveOfTypeAsync(It.IsAny<string>())).ReturnsAsync(true); // suppress creation

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("OMDbApiTier", "free")).ReturnsAsync("free");

        var provider = new OMDbProvider(
            new HttpClient(handler),
            new Mock<ILogger<OMDbProvider>>().Object,
            new RateLimiterFactory(),
            new Mock<IConfiguration>().Object,
            settings.Object,
            notifications.Object,
            tracker.Object);
        return (provider, handler, tracker, notifications);
    }

    private static readonly string ValidMovieBody =
        """{"Response":"True","Title":"Small Soldiers","Year":"1998","imdbID":"tt0122718","Plot":"Toys go to war."}""";

    [Fact]
    public async Task SoftmediaMode_CountsEveryRequest_AgainstFreeTier()
    {
        // SM-WI-011: the bundled shared key must be quota-counted too (it never was).
        var (provider, _, tracker, _) = CreateProvider(new StubHandler().Enqueue(ValidMovieBody));

        var item = new MediaItem { Title = "Small Soldiers", Year = 1998, Type = MediaType.Movie };
        var result = await provider.FetchMetadataWithKeyAsync(item, "test-key", mode: "softmedia");

        Assert.NotNull(result);
        tracker.Verify(t => t.TryRecordRequestAsync(1_000), Times.Once);
    }

    [Fact]
    public async Task LimitErrorBody_SuspendsProvider_AndSkipsFallbackSearch()
    {
        // The quota-refusal body must NOT read as "not found": exactly one HTTP request
        // (no &s= fallback), tracker marked exhausted, null result.
        var handler = new StubHandler().Enqueue("""{"Response":"False","Error":"Request limit reached!"}""");
        var (provider, h, tracker, _) = CreateProvider(handler);

        var item = new MediaItem { Title = "Small Soldiers", Year = 1998, Type = MediaType.Movie };
        var result = await provider.FetchMetadataWithKeyAsync(item, "test-key", mode: "softmedia");

        Assert.Null(result);
        Assert.Single(h.Requests);
        tracker.Verify(t => t.MarkExhaustedAsync(1_000), Times.Once);
    }

    [Fact]
    public async Task NotFound_StillRunsFallbackSearch_WithoutSuspending()
    {
        var handler = new StubHandler()
            .Enqueue("""{"Response":"False","Error":"Movie not found!"}""")
            .Enqueue("""{"Response":"False","Error":"Movie not found!"}""");
        var (provider, h, tracker, _) = CreateProvider(handler);

        var item = new MediaItem { Title = "A Boy and His Dog", Type = MediaType.Movie };
        var result = await provider.FetchMetadataWithKeyAsync(item, "test-key", mode: "softmedia");

        Assert.Null(result);
        Assert.Equal(2, h.Requests.Count); // exact match + &s= fallback
        Assert.Contains("&s=", h.Requests[1]);
        tracker.Verify(t => t.MarkExhaustedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TrackerDenial_BlocksCall_WithZeroHttpRequests()
    {
        var (provider, handler, _, _) = CreateProvider(trackerAllows: false);

        var item = new MediaItem { Title = "Small Soldiers", Year = 1998, Type = MediaType.Movie };
        var result = await provider.FetchMetadataWithKeyAsync(item, "test-key", mode: "softmedia");

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"Response":"False","Error":"Request limit reached!"}""", true)]
    [InlineData("""{"Response":"False","Error":"Invalid API key!"}""", true)]
    [InlineData("""{"Response":"False","Error":"Movie not found!"}""", false)]
    [InlineData("""{"Response":"True","Title":"X"}""", false)]
    [InlineData("not json at all", false)]
    public void IsProviderUnavailableResponse_ClassifiesBodies(string body, bool expected)
    {
        Assert.Equal(expected, OMDbProvider.IsProviderUnavailableResponse(body, out _));
    }

    [Fact]
    public async Task FetchMetadataAsync_ThrowsInvalidOperationException()
    {
        // Arrange — OMDb requires an API key; direct calls bypass key resolution
        var handler = new HttpClientHandler();
        var httpClient = new HttpClient(handler);
        var logger = new Mock<ILogger<OMDbProvider>>();
        var config = new Mock<IConfiguration>();
        var settings = new Mock<ISettingsService>();
        var notifications = new Mock<INotificationService>();
        var rateLimiterFactory = new RateLimiterFactory();
        var usageTracker = new Mock<IOmdbUsageTracker>();

        var provider = new OMDbProvider(httpClient, logger.Object, rateLimiterFactory, config.Object, settings.Object, notifications.Object, usageTracker.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Movie", Type = MediaType.Movie };

        // Act & Assert — should throw, never silently return null
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchMetadataAsync(item));
    }
}
