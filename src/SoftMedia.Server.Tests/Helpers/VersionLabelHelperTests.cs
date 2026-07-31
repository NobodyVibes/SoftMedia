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
    [InlineData(null, null, null)]
    [InlineData(0, 0, null)]
    [InlineData(7680, 4320, "8K")]
    [InlineData(3840, 2160, "4K")]
    [InlineData(2560, 1440, "1440p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(640, 480, "480p")]
    [InlineData(480, 360, "360p")]
    // Widescreen "scope" encodes crop the HEIGHT — width must carry the tier, or every
    // 2.35:1 movie gets under-labeled (the Goldmember bug: 1920×816 read as "720p").
    [InlineData(1920, 816, "1080p")]
    [InlineData(3840, 1608, "4K")]
    [InlineData(1280, 544, "720p")]
    // Height alone still works when width is unknown.
    [InlineData(null, 1080, "1080p")]
    public void ResolutionLabel_MapsDimensions_WidthAware(int? width, int? height, string? expected)
        => Assert.Equal(expected, VersionLabelHelper.ResolutionLabel(width, height));

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
            Title = "Movie", Width = 3840, Height = 2160, HdrFormat = "HDR10",
            Path = @"C:\m\Movie.2010.Extended.2160p.mkv",
        };
        Assert.Equal("4K HDR10 Extended", VersionLabelHelper.BuildLabel(item));
    }

    [Fact]
    public void BuildLabel_ScopeEncode_UsesWidthForTheTier()
    {
        // 2.35:1 at 1080p: 1920×816 — must read 1080p, matching the quality header.
        var item = new MediaItem { Title = "Movie", Width = 1920, Height = 816, Path = @"C:\m\m.mkv" };
        Assert.Equal("1080p", VersionLabelHelper.BuildLabel(item));
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
