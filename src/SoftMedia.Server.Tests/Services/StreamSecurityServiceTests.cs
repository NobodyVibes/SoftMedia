using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security;

namespace SoftMedia.Server.Tests.Services;

public class StreamSecurityServiceTests
{
    private readonly Mock<ILogger<StreamSecurityService>> _loggerMock;
    private readonly StreamSecurityService _service;

    public StreamSecurityServiceTests()
    {
        _loggerMock = new Mock<ILogger<StreamSecurityService>>();
        _service = new StreamSecurityService(_loggerMock.Object);
    }

    [Fact]
    public void IsPathAuthorized_ShouldReturnFalse_WhenLibraryPathsEmpty()
    {
        var result = _service.IsPathAuthorized("C:/test/file.mkv", new List<string>());
        Assert.False(result);
    }

    [Fact]
    public void IsPathAuthorized_ShouldReturnTrue_WhenPathInLibrary()
    {
        var libraryPaths = new[] { "C:/Media/Movies" };
        var result = _service.IsPathAuthorized("C:/Media/Movies/Action/DieHard.mkv", libraryPaths);
        Assert.True(result);
    }

    [Fact]
    public void IsPathAuthorized_ShouldReturnFalse_WhenPathOutsideLibrary()
    {
        var libraryPaths = new[] { "C:/Media/Movies" };
        var result = _service.IsPathAuthorized("C:/Media/Music/Song.mp3", libraryPaths);
        Assert.False(result);
    }

    [Fact]
    public void IsPathAuthorized_ShouldPreventPartialMatch()
    {
        var libraryPaths = new[] { "C:/Media/Movies" };
        // "Movies_Private" starts with "Movies" but is a different folder
        var result = _service.IsPathAuthorized("C:/Media/Movies_Private/Secret.mkv", libraryPaths);
        Assert.False(result);
    }

    [Fact]
    public void ValidateMediaAccess_ShouldReturnFileNotFound_WhenItemIsNull()
    {
        var result = _service.ValidateMediaAccess(null!);
        Assert.Equal(MediaAccessResult.FileNotFound, result);
    }

    [Fact]
    public void ValidateMediaAccess_ShouldReturnFileNotFound_WhenLibraryIsNull()
    {
        var item = new MediaItem { Path = "C:/test.mkv", Library = null };
        var result = _service.ValidateMediaAccess(item);
        Assert.Equal(MediaAccessResult.FileNotFound, result);
    }
}
