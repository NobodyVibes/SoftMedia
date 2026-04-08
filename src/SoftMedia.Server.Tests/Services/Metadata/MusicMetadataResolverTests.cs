using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// Tests for the Music routing logic in MetadataRouter (formerly MusicMetadataResolver).
/// Validates provider selection, sufficiency checks, and fallback merge behavior.
/// </summary>
public class MetadataRouterMusicTests
{
    private readonly Mock<IMetadataProvider> _embeddedProviderMock;
    private readonly Mock<IMetadataProvider> _musicBrainzProviderMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<ILogger<MetadataRouter>> _loggerMock;
    private readonly MetadataRouter _router;

    public MetadataRouterMusicTests()
    {
        _embeddedProviderMock = new Mock<IMetadataProvider>();
        _embeddedProviderMock.SetupGet(p => p.ProviderName).Returns("Embedded");
        _embeddedProviderMock.SetupGet(p => p.SupportedType).Returns(LibraryType.Music);

        _musicBrainzProviderMock = new Mock<IMetadataProvider>();
        _musicBrainzProviderMock.SetupGet(p => p.ProviderName).Returns("MusicBrainz");
        _musicBrainzProviderMock.SetupGet(p => p.SupportedType).Returns(LibraryType.Music);

        _settingsServiceMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<MetadataRouter>>();

        var providers = new List<IMetadataProvider> { _embeddedProviderMock.Object, _musicBrainzProviderMock.Object };

        _router = new MetadataRouter(providers, _settingsServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task FetchMetadata_Music_ShouldUseEmbeddedOnly_WhenConfigured()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("Embedded");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        var expectedResult = new MetadataResult { Title = "Embedded Song" };

        _embeddedProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert
        Assert.Equal(expectedResult, result);
        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Never);
    }

    [Fact]
    public async Task FetchMetadata_Music_ShouldUseMusicBrainzOnly_WhenConfigured()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("MusicBrainzOnly");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        var expectedResult = new MetadataResult { Title = "MB Song" };

        _musicBrainzProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert
        Assert.Equal(expectedResult, result);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Never);
    }

    [Fact]
    public async Task FetchMetadata_Music_ShouldFallbackToMusicBrainz_WhenEmbeddedInsufficient()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("MusicBrainz");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        
        // Missing artist and poster, therefore insufficient
        var embeddedResult = new MetadataResult { Title = "Song Title" }; 
        var fallbackResult = new MetadataResult { Artist = "The Artist", PosterUrl = "http://art" };

        _embeddedProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(embeddedResult);
        _musicBrainzProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(fallbackResult);

        // Act
        var result = await _router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Song Title", result.Title); // From primary
        Assert.Equal("The Artist", result.Artist); // Merged from fallback
        Assert.Equal("http://art", result.PosterUrl); // Merged from fallback

        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
    }

    [Fact]
    public async Task FetchMetadata_Music_ShouldSkipFallback_WhenEmbeddedSufficient()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("MusicBrainz");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        
        // Sufficient: has title, artist, AND poster
        var embeddedResult = new MetadataResult 
        { 
            Title = "Song Title", 
            Artist = "The Band",
            PosterUrl = "http://cover.jpg"
        };

        _embeddedProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(embeddedResult);

        // Act
        var result = await _router.FetchMetadataAsync(item, LibraryType.Music);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Song Title", result.Title);
        Assert.Equal("The Band", result.Artist);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Never); // No fallback triggered
    }
}
