using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// DV-WI-013 — the single version-label authority: resolution mapping, filename edition
/// tokens, and the composed display label with its never-probed fallback.
/// </summary>
public class VersionLabelHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(4320, "8K")]
    [InlineData(2160, "4K")]
    [InlineData(1440, "1440p")]
    [InlineData(1080, "1080p")]
    [InlineData(720, "720p")]
    [InlineData(480, "480p")]
    [InlineData(360, "360p")]
    public void ResolutionLabel_MapsHeights(int? height, string? expected)
        => Assert.Equal(expected, VersionLabelHelper.ResolutionLabel(height));

    [Theory]
    [InlineData(@"C:\movies\Blade Runner (1982) Directors Cut.mkv", "Director's Cut")]
    [InlineData(@"C:\movies\Blade.Runner.1982.Director's.Cut.mkv", "Director's Cut")]
    [InlineData(@"C:\movies\Movie.EXTENDED.1080p.mkv", "Extended")]
    [InlineData(@"C:\movies\Movie.IMAX.mkv", "IMAX")]
    [InlineData(@"C:\movies\Movie (2010).mkv", null)]
    public void EditionLabel_ParsesFilenameTokens(string path, string? expected)
        => Assert.Equal(expected, VersionLabelHelper.EditionLabel(path));

    [Fact]
    public void BuildLabel_ComposesResolutionHdrAndEdition()
    {
        var item = new MediaItem
        {
            Title = "Movie", Height = 2160, HdrFormat = "HDR10",
            Path = @"C:\m\Movie.2010.Extended.2160p.mkv",
        };
        Assert.Equal("4K HDR10 Extended", VersionLabelHelper.BuildLabel(item));
    }

    [Fact]
    public void BuildLabel_FallsBackToContainer_ThenOriginal()
    {
        Assert.Equal("MKV", VersionLabelHelper.BuildLabel(
            new MediaItem { Title = "M", Container = "mkv", Path = @"C:\m\m.mkv" }));
        Assert.Equal("Original", VersionLabelHelper.BuildLabel(
            new MediaItem { Title = "M", Path = @"C:\m\m.bin" }));
    }
}
