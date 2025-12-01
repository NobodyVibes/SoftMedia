using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Tests.Services.Metadata;

public class MetadataRouterTests
{
    [Fact]
    public async Task FetchMetadataAsync_SelectsCorrectProvider()
    {
        // Arrange
        var movieProvider = new Mock<IMetadataProvider>();
        movieProvider.Setup(p => p.SupportedType).Returns(LibraryType.Movie);
        movieProvider.Setup(p => p.FetchMetadataAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("MovieData");

        var tvProvider = new Mock<IMetadataProvider>();
        tvProvider.Setup(p => p.SupportedType).Returns(LibraryType.TV);
        tvProvider.Setup(p => p.FetchMetadataAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("TVData");

        var router = new MetadataRouter(new[] { movieProvider.Object, tvProvider.Object });

        // Act
        var movieResult = await router.FetchMetadataAsync("Matrix", "/path/to/matrix", LibraryType.Movie);
        var tvResult = await router.FetchMetadataAsync("Friends", "/path/to/friends", LibraryType.TV);

        // Assert
        Assert.Equal("MovieData", movieResult);
        Assert.Equal("TVData", tvResult);
    }

    [Fact]
    public async Task FetchMetadataAsync_ReturnsNull_WhenNoProviderFound()
    {
        // Arrange
        var router = new MetadataRouter(Enumerable.Empty<IMetadataProvider>());

        // Act
        var result = await router.FetchMetadataAsync("Unknown", "/path/to/unknown", LibraryType.Book);

        // Assert
        Assert.Null(result);
    }
}
