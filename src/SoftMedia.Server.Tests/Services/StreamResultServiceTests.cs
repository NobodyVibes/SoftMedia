using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Tests.Services;

public class StreamResultServiceTests
{
    private readonly Mock<ITranscodeService> _transcodeServiceMock;
    private readonly Mock<IHlsManifestService> _hlsManifestServiceMock;
    private readonly Mock<ILogger<StreamResultService>> _loggerMock;
    private readonly StreamResultService _service;

    public StreamResultServiceTests()
    {
        _transcodeServiceMock = new Mock<ITranscodeService>();
        _hlsManifestServiceMock = new Mock<IHlsManifestService>();
        _loggerMock = new Mock<ILogger<StreamResultService>>();
        
        _service = new StreamResultService(
            _transcodeServiceMock.Object, 
            _hlsManifestServiceMock.Object, 
            _loggerMock.Object);
    }

    [Fact]
    public async Task GenerateMasterPlaylist_Returns503_WhenPlaylistNotReady()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        
        _transcodeServiceMock.Setup(x => x.GetPlaylist(mediaId, userId, null))
            .Returns((Stream)null); // Transcode hasn't produced playlist yet

        // Act
        var result = await _service.GenerateMasterPlaylistResultAsync(mediaId, userId, null, "token");

        // Assert
        var statusCodeResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(503, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GenerateMasterPlaylist_ReturnsStream_WhenNoToken()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var stream = new MemoryStream(new byte[10]);
        
        _transcodeServiceMock.Setup(x => x.GetPlaylist(mediaId, userId, null))
            .Returns(stream);

        // Act
        var result = await _service.GenerateMasterPlaylistResultAsync(mediaId, userId, null, null);

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", fileResult.ContentType);
        Assert.Same(stream, fileResult.FileStream);
    }

    [Fact]
    public async Task GenerateMasterPlaylist_InjectsToken_WhenTokenProvided()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rawStream = new MemoryStream();
        var processedBytes = new byte[] { 1, 2, 3 };
        var token = "test_token";

        // Setup session to have subtitle path
        var session = new TranscodeSession { SubtitleVttPath = "/tmp/subs.vtt" };
        _transcodeServiceMock.Setup(x => x.GetPlaylist(mediaId, userId, null)).Returns(rawStream);
        _transcodeServiceMock.Setup(x => x.GetSession(mediaId, userId, null)).Returns(session);
        
        _hlsManifestServiceMock.Setup(x => x.GenerateMasterPlaylistAsync(
            It.IsAny<Stream>(), 
            token, 
            mediaId.ToString(), 
            null, 
            "/tmp/subs.vtt"))
            .ReturnsAsync(processedBytes);

        // Act
        var result = await _service.GenerateMasterPlaylistResultAsync(mediaId, userId, null, token);

        // Assert
        var contentResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal(processedBytes, contentResult.FileContents);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
        
        // Verify stream disposed? (Hard to verification memory stream disposal without wrapper, but logic is there)
    }

    [Fact]
    public void GetSegment_ReturnsNotFound_WhenMissing()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var segment = "seg_0.ts";
        
        _transcodeServiceMock.Setup(x => x.GetSegment(mediaId, userId, segment, null))
            .Returns((Stream)null);

        // Act
        var result = _service.GetSegmentResult(mediaId, userId, null, segment);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetSegment_ReturnsCorrectMimeType_ForTS()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var segment = "seg_0.ts";
        var stream = new MemoryStream();
        
        _transcodeServiceMock.Setup(x => x.GetSegment(mediaId, userId, segment, null))
            .Returns(stream);

        // Act
        var result = _service.GetSegmentResult(mediaId, userId, null, segment);

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/MP2T", fileResult.ContentType);
    }

    [Fact]
    public void GetSegment_ReturnsCorrectMimeType_ForMP4()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var segment = "seg_0.m4s";
        var stream = new MemoryStream();
        
        _transcodeServiceMock.Setup(x => x.GetSegment(mediaId, userId, segment, null))
            .Returns(stream);

        // Act
        var result = _service.GetSegmentResult(mediaId, userId, null, segment);

        // Assert
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/mp4", fileResult.ContentType);
    }
}
