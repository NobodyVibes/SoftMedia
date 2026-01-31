using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Tests.Services;

public class TranscodeSessionServiceTests
{
    private readonly Mock<ITranscodeService> _transcodeServiceMock;
    private readonly Mock<ILogger<TranscodeSessionService>> _loggerMock;
    private readonly TranscodeSessionService _service;

    public TranscodeSessionServiceTests()
    {
        _transcodeServiceMock = new Mock<ITranscodeService>();
        _loggerMock = new Mock<ILogger<TranscodeSessionService>>();
        
        _service = new TranscodeSessionService(
            _transcodeServiceMock.Object, 
            _loggerMock.Object);
    }

    [Fact]
    public void UpdateClientPosition_CallsTranscodeService_WhenSegmentIndexValid()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var segment = "seg_5.ts";
        
        // Act
        _service.UpdateClientPosition(mediaId, userId, null, segment);

        // Assert
        _transcodeServiceMock.Verify(x => x.UpdateClientPosition(
            It.Is<TranscodeSessionKey>(k => k.MediaId == mediaId && k.UserId == userId), 
            5), Times.Once);
    }

    [Fact]
    public void PauseSession_ReturnsSuccess_WhenPaused()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        
        _transcodeServiceMock.Setup(x => x.SetPaused(It.IsAny<TranscodeSessionKey>(), userId, true))
            .Returns(true);

        // Act
        var result = _service.PauseSession(mediaId, userId, null);

        // Assert
        Assert.Equal(TranscodeSessionResult.Success, result);
    }

    [Fact]
    public void PauseSession_ReturnsNotFound_WhenSessionMissing()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        
        _transcodeServiceMock.Setup(x => x.SetPaused(It.IsAny<TranscodeSessionKey>(), userId, true))
            .Returns(false);
        _transcodeServiceMock.Setup(x => x.GetSession(It.IsAny<TranscodeSessionKey>()))
            .Returns((TranscodeSession)null);

        // Act
        var result = _service.PauseSession(mediaId, userId, null);

        // Assert
        Assert.Equal(TranscodeSessionResult.NotFound, result);
    }

    [Fact]
    public void PauseSession_ReturnsUnauthorized_WhenUserMismatch()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        
        _transcodeServiceMock.Setup(x => x.SetPaused(It.IsAny<TranscodeSessionKey>(), userId, true))
            .Returns(false);
        _transcodeServiceMock.Setup(x => x.GetSession(It.IsAny<TranscodeSessionKey>()))
            .Returns(new TranscodeSession { UserId = otherUser });

        // Act
        var result = _service.PauseSession(mediaId, userId, null);

        // Assert
        Assert.Equal(TranscodeSessionResult.Unauthorized, result);
    }
}
