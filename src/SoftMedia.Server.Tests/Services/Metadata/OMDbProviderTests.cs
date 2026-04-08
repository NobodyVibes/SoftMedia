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

        var provider = new OMDbProvider(httpClient, logger.Object, rateLimiterFactory, config.Object, settings.Object, notifications.Object);
        var item = new MediaItem { Id = Guid.NewGuid(), Title = "Test Movie", Type = MediaType.Movie };

        // Act & Assert — should throw, never silently return null
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchMetadataAsync(item));
    }
}
