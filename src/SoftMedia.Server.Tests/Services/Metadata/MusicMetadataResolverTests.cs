using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MusicMetadataResolverTests
{
    private readonly Mock<IMetadataProvider> _embeddedProviderMock;
    private readonly Mock<IMetadataProvider> _musicBrainzProviderMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<ILogger<MusicMetadataResolver>> _loggerMock;
    private readonly MusicMetadataResolver _resolver;

    public MusicMetadataResolverTests()
    {
        _embeddedProviderMock = new Mock<IMetadataProvider>();
        _embeddedProviderMock.SetupGet(p => p.ProviderName).Returns("Embedded");
        _embeddedProviderMock.SetupGet(p => p.SupportedType).Returns(LibraryType.Music);

        _musicBrainzProviderMock = new Mock<IMetadataProvider>();
        _musicBrainzProviderMock.SetupGet(p => p.ProviderName).Returns("MusicBrainz");
        _musicBrainzProviderMock.SetupGet(p => p.SupportedType).Returns(LibraryType.Music);

        _settingsServiceMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<MusicMetadataResolver>>();

        var providers = new List<IMetadataProvider> { _embeddedProviderMock.Object, _musicBrainzProviderMock.Object };

        _resolver = new MusicMetadataResolver(providers, _settingsServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ResolveMetadataAsync_ShouldUseEmbeddedOnly_WhenConfigured()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("Embedded");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        var expectedResult = new MetadataResult { Title = "Embedded Song" };

        _embeddedProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _resolver.ResolveMetadataAsync(item);

        // Assert
        Assert.Equal(expectedResult, result);
        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Never);
    }

    [Fact]
    public async Task ResolveMetadataAsync_ShouldUseMusicBrainzOnly_WhenConfigured()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MusicProvider", It.IsAny<string>()))
            .ReturnsAsync("MusicBrainzOnly");

        var item = new MediaItem { Title = "Song", Type = MediaType.Audio };
        var expectedResult = new MetadataResult { Title = "MB Song" };

        _musicBrainzProviderMock.Setup(p => p.FetchMetadataAsync(item))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _resolver.ResolveMetadataAsync(item);

        // Assert
        Assert.Equal(expectedResult, result);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Never);
    }

    [Fact]
    public async Task ResolveMetadataAsync_ShouldFallbackToMusicBrainz_WhenEmbeddedInsufficient()
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
        var result = await _resolver.ResolveMetadataAsync(item);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Song Title", result.Title); // From primary
        Assert.Equal("The Artist", result.Artist); // Merged from fallback
        Assert.Equal("http://art", result.PosterUrl); // Merged from fallback

        _embeddedProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
        _musicBrainzProviderMock.Verify(p => p.FetchMetadataAsync(item), Times.Once);
    }
}
