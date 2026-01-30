using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services;
using System.Text;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services;

public class HlsManifestServiceTests
{
    private readonly Mock<ILogger<HlsManifestService>> _loggerMock;
    private readonly HlsManifestService _service;

    public HlsManifestServiceTests()
    {
        _loggerMock = new Mock<ILogger<HlsManifestService>>();
        _service = new HlsManifestService(_loggerMock.Object);
    }

    [Fact]
    public async Task GenerateMasterPlaylist_ShouldInjectToken()
    {
        // Arrange
        var basePlaylist = "#EXTM3U\n#EXTINF:10.0,\nseg_000.ts\n#EXT-X-ENDLIST";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(basePlaylist));
        var token = "abc-123";

        // Act
        var resultBytes = await _service.GenerateMasterPlaylistAsync(stream, token, "id", null, null);
        var result = Encoding.UTF8.GetString(resultBytes);

        // Assert
        Assert.Contains("seg_000.ts?token=abc-123", result);
    }

    [Fact]
    public async Task GenerateMasterPlaylist_ShouldInjectSubtitle_WhenPathProvided()
    {
        // Arrange
        var basePlaylist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.0,\nseg_000.ts";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(basePlaylist));
        var token = "abc-123";
        // Create a dummy vtt file
        var vttPath = Path.GetTempFileName();
        File.WriteAllText(vttPath, "WEBVTT");

        try
        {
            // Act
            var resultBytes = await _service.GenerateMasterPlaylistAsync(stream, token, "mediaId", 5, vttPath);
            var result = Encoding.UTF8.GetString(resultBytes);

            // Assert
            Assert.Contains("#EXT-X-MEDIA:TYPE=SUBTITLES", result);
            Assert.Contains("URI=\"/api/transcode/mediaId/subtitles.vtt?token=abc-123&sub=5\"", result);
        }
        finally
        {
            if (File.Exists(vttPath)) File.Delete(vttPath);
        }
    }
}
