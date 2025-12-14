using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests;

public class MusicBrainzProviderTests
{
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<MusicBrainzProvider>> _loggerMock;
    private readonly MusicBrainzProvider _provider;

    public MusicBrainzProviderTests()
    {
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _loggerMock = new Mock<ILogger<MusicBrainzProvider>>();
        _provider = new MusicBrainzProvider(_httpClient, _loggerMock.Object);
    }

    [Fact]
    public async Task FetchMetadataAsync_ShouldUseReleaseGroupPoster_WhenAvailable()
    {
        // Arrange
        var title = "Test Track";
        var artist = "Test Artist";
        var album = "Test Album";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = req.RequestUri?.Query ?? "";
                if (query.Contains("release")) // Strict
                {
                    return new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent("{\"recordings\":[]}") };
                }
                
                // Broad - Return result with Release Group
                var json = """
                {
                    "recordings": [
                        {
                            "title": "Test Track",
                            "artist-credit": [{"name": "Test Artist"}],
                            "releases": [
                                {
                                    "id": "rel-123",
                                    "title": "Test Album",
                                    "release-group": { "id": "rg-999" }
                                }
                            ]
                        }
                    ]
                }
                """;
                return new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(json) };
            });

        // Act
        var resultJson = await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = $"/music/{artist}/{album}/{title}.mp3" });

        // Assert
        Assert.NotNull(resultJson);
        Assert.Contains("coverartarchive.org/release-group/rg-999/front", resultJson);
    }

    [Fact]
    public async Task FetchMetadataAsync_ShouldSelectCorrectAlbum_FromMultipleResults()
    {
        // Arrange
        var title = "Hit Song";
        var artist = "Pop Star";
        var targetAlbum = "Greatest Hits";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($$"""
                {
                    "recordings": [
                        {
                            "title": "Hit Song",
                            "releases": [
                                { "title": "Debut Algebra", "id": "rel-1" }
                            ]
                        },
                        {
                            "title": "Hit Song",
                            "releases": [
                                { "title": "{{targetAlbum}}", "id": "rel-target", "release-group": { "id": "rg-target" } }
                            ]
                        },
                        {
                            "title": "Hit Song",
                            "releases": [
                                { "title": "Single Remix", "id": "rel-3" }
                            ]
                        }
                    ]
                }
                """)
            });

        // Act
        // We simulate a case where strict failed (or wasn't matched purely) so we rely on ranking
        // But here we just mock specific response for ANY query.
        var resultJson = await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = $"/music/{artist}/{targetAlbum}/{title}.mp3" });

        // Assert
        Assert.NotNull(resultJson);
        Assert.Contains("rg-target", resultJson); // Should pick the one matching "Greatest Hits"
        Assert.Contains("Greatest Hits", resultJson);
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldMatchAlbum_WhenLocalNameIsLonger()
    {
        // Arrange
        var title = "Scream Of Anger";
        var artist = "Arch Enemy";
        var localAlbum = "1999 - Burning Bridges (2009 Deluxe Edition)";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("""
                {
                    "recordings": [
                        {
                            "title": "Scream of Anger",
                            "releases": [
                                { "title": "Burning Bridges", "id": "rel-std", "release-group": { "id": "rg-match" } },
                                { "title": "Stigmata", "id": "rel-other" }
                            ]
                        }
                    ]
                }
                """)
            });

        // Act
        var resultJson = await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = $"/music/{artist}/{localAlbum}/{title}.mp3" });

        // Assert
        Assert.NotNull(resultJson);
        Assert.Contains("rg-match", resultJson);
        Assert.Contains("Burning Bridges", resultJson);
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldCleanTrackTitle_BeforeSearching()
    {
        // Arrange
        var title = "12 - Scream Of Anger (Europe cover)";
        var artist = "Arch Enemy";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // Assert inside mock (simplest way to catch the outgoing query)
                // We expect cleaned title
                if (query.Contains("recording:\"Scream Of Anger\"") && !query.Contains("Europe cover"))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") // Return empty valid json
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = $"/music/{artist}/Album/{title}.mp3" });

        // Assert
        // If query was wrong, mock returns NotFound -> Logs warning -> Returns null.
        // If query was right, mock returns OK/Empty -> Returns null.
        // We can verify mock was called.
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("recording:\"Scream Of Anger\"") &&
                !WebUtility.UrlDecode(req.RequestUri.Query).Contains("Europe cover")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldIgnoreDiscFolder_AndFindCorrectAlbum()
    {
        // Arrange
        var title = "Test Track";
        var artist = "Test Artist";
        // Path simulates: /Music/Test Artist/Double Disc Album/CD 1/Test Track.mp3
        var path = $"/Music/{artist}/Double Disc Album/CD 1/{title}.mp3";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // We expect "Double Disc Album" to be detected as the album, NOT "CD 1"
                if (query.Contains("release:\"Double Disc Album\"") && !query.Contains("CD 1"))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") 
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        // We pass "null" title/artist/album in a real scenario? 
        // No, the provider usually interprets them from path if not provided?
        // Actually FetchMetadataAsync takes (title, path). It assumes artist/album might be missing or need parsing.
        // We call it with just title and path.
        await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = path });

        // Assert
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("release:\"Double Disc Album\"")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldHandleHyphenatedTitles()
    {
        // Arrange
        var title = "06 I Am Legend-Out for Blood";
        var artist = "Arch Enemy";
        var path = $"/Music/{artist}/Doomsday Machine/{title}.mp3";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // Expect replacement of hyphen with space
                if (query.Contains("recording:\"I Am Legend Out for Blood\""))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") 
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = path });

        // Assert
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("recording:\"I Am Legend Out for Blood\"")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldIgnoreComplexDiscFolder()
    {
        // Arrange
        var title = "Test Track";
        var artist = "Test Artist";
        // Path simulates: /Music/Test Artist/Test Album/CD1 - Album/Test Track.mp3
        var path = $"/Music/{artist}/Test Album/CD1 - Album/{title}.mp3";

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // We expect "Test Album" to be detected as the album
                if (query.Contains("release:\"Test Album\""))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") 
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        await _provider.FetchMetadataAsync(new MediaItem { Title = title, Path = path });

        // Assert
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("release:\"Test Album\"")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }
    [Fact]
    public async Task FetchMetadataAsync_ShouldUseEmbeddedTags_WhenAvailable()
    {
        // Arrange
        var title = "Tagged Track";
        var path = "/Music/BadFolder/BadAlbum/Track.mp3"; // Misleading path
        
        var tags = new Dictionary<string, object>
        {
            { "artist", "Real Artist" },
            { "album", "Real Album" }
        };
        
        var item = new MediaItem 
        { 
            Title = title, 
            Path = path,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(tags)
        };

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // Expect query to use Real Artist/Album, NOT BadFolder/BadAlbum
                if (query.Contains("artist:\"Real Artist\"") && query.Contains("release:\"Real Album\""))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") 
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        await _provider.FetchMetadataAsync(item);

        // Assert
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("artist:\"Real Artist\"") &&
                WebUtility.UrlDecode(req.RequestUri.Query).Contains("release:\"Real Album\"") &&
                !WebUtility.UrlDecode(req.RequestUri.Query).Contains("BadAlbum")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
        }

    [Fact]
    public async Task FetchMetadataAsync_ShouldCleanEmbeddedTags_BeforeSearching()
    {
        // Arrange
        var title = "Fields Of Desolation / Outro";
        var artist = "Arch Enemy";
        var rawAlbum = "Tyrants Of The Rising Sun: Live In Japan (CD2)"; // Dirty tag
        
        var tags = new Dictionary<string, object>
        {
            { "artist", artist },
            { "album", rawAlbum },
            { "title", title }
        };
        
        var item = new MediaItem 
        { 
            Title = title, 
            Path = "/Music/Arch Enemy/Tyrants/CD2/track.mp3",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(tags)
        };

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage req, CancellationToken token) =>
            {
                var query = WebUtility.UrlDecode(req.RequestUri?.Query ?? "");
                
                // We EXPECT the provider to strip "(CD2)" and replace "/" with space
                // Query should be for "Tyrants Of The Rising Sun: Live In Japan" AND "Fields Of Desolation Outro"
                if (query.Contains("release:\"Tyrants Of The Rising Sun: Live In Japan\"") && 
                    !query.Contains("(CD2)") &&
                    query.Contains("Fields Of Desolation Outro"))
                {
                    return new HttpResponseMessage 
                    { 
                        StatusCode = HttpStatusCode.OK, 
                        Content = new StringContent("{\"recordings\":[]}") 
                    };
                }
                
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        // Act
        await _provider.FetchMetadataAsync(item);

        // Assert
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => 
                WebUtility.UrlDecode(req.RequestUri!.Query).Contains("release:\"Tyrants Of The Rising Sun: Live In Japan\"") &&
                !WebUtility.UrlDecode(req.RequestUri.Query).Contains("(CD2)")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }
}


