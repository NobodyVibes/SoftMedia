using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services.Transcoding;
using Xunit;
using System.Text;
using System.IO;

namespace SoftMedia.Tests.Services.Transcoding;

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
    public async Task GenerateMasterPlaylistAsync_InjectsSubtitleAttribute_WhenSubtitlesPresent()
    {
        // Arrange
        var baseManifest = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1920x1080\nindex.m3u8";
        var baseStream = new MemoryStream(Encoding.UTF8.GetBytes(baseManifest));
        var mediaId = "media1";
        var token = "token123";
        var subTrackIndex = 1;
        var subtitleVttPath = "subtitles.vtt";

        // Create a dummy file to pass the File.Exists check
        // Since HlsManifestService checks File.Exists(subtitleVttPath), we need to ensure this passes.
        // However, we can't easily mock static File.Exists in a unit test without a wrapper or using integration test.
        // For this unit test, we might get blocked by File.Exists = false.
        // Let's create a temporary file.
        var tempFile = Path.GetTempFileName();
        
        try 
        {
            // Act
            var resultBytes = await _service.GenerateMasterPlaylistAsync(
                baseStream, token, mediaId, subTrackIndex, tempFile);
            
            var resultString = Encoding.UTF8.GetString(resultBytes);

            // Assert
            // It should inject the SUBTITLES="subs" attribute into the STREAM-INF line
            Assert.Contains("SUBTITLES=\"subs\"", resultString);
            
            // It should also define the media group (which it already does)
            Assert.Contains("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\"", resultString);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
