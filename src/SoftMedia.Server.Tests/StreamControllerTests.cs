using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests;

public class StreamControllerTests
{
    private readonly Mock<IMediaService> _mediaServiceMock;
    private readonly Mock<ILogger<StreamController>> _loggerMock;
    private readonly StreamController _controller;

    public StreamControllerTests()
    {
        _mediaServiceMock = new Mock<IMediaService>();
        _loggerMock = new Mock<ILogger<StreamController>>();
        _controller = new StreamController(_mediaServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GetStream_ReturnsNotFound_WhenServiceReturnsNull()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mediaServiceMock.Setup(x => x.GetStreamInfoAsync(id)).ReturnsAsync((StreamInfoDto?)null);

        // Act
        var result = await _controller.GetStream(id);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetStream_ReturnsForbid_WhenServiceThrowsUnauthorized()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mediaServiceMock.Setup(x => x.GetStreamInfoAsync(id)).ThrowsAsync(new UnauthorizedAccessException());

        // Act
        var result = await _controller.GetStream(id);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetStream_ReturnsPhysicalFile_WhenValid()
    {
        // Arrange
        var id = Guid.NewGuid();
        var dto = new StreamInfoDto { Path = @"C:\Test\file.mp4", ContentType = "video/mp4" };
        _mediaServiceMock.Setup(x => x.GetStreamInfoAsync(id)).ReturnsAsync(dto);

        // Act
        var result = await _controller.GetStream(id);

        // Assert
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(dto.Path, fileResult.FileName);
        Assert.Equal(dto.ContentType, fileResult.ContentType);
        Assert.True(fileResult.EnableRangeProcessing);
    }
}
