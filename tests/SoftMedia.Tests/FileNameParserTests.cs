using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Tests;

public class FileNameParserTests
{
    [Theory]
    [InlineData("The.Matrix.1999.1080p.mkv", "The Matrix", 1999)]
    [InlineData("Inception (2010).mp4", "Inception", 2010)]
    [InlineData("Avatar.2009.Extended.Collector's.Edition.1080p.BluRay.x264.mkv", "Avatar", 2009)]
    [InlineData("Interstellar_2014_IMAX_1080p.mkv", "Interstellar", 2014)]
    [InlineData("Unknown Movie.mkv", "Unknown Movie", null)]
    [InlineData("1997.Austin.Powers-.International.Man.Of.Mystery.1920x818.BDRip.TrueHD.mkv", "Austin Powers International Man Of Mystery", 1997)]
    [InlineData("2002.Austin.Powers-.Goldmember.1920x816.BDRip.x264.TrueHD.mkv", "Austin Powers Goldmember", 2002)]
    [InlineData("1999.Austin.Powers-.The.Spy.Who.Shagged.Me.mkv", "Austin Powers The Spy Who Shagged Me", 1999)]
    public void ParseMovie_ShouldExtractTitleAndYear(string fileName, string expectedTitle, int? expectedYear)
    {
        var (title, year) = FileNameParser.ParseMovie(fileName);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E01.mkv", "Breaking Bad", 1, 1, "")]
    [InlineData("Game of Thrones - 1x01 - Winter Is Coming.mkv", "Game of Thrones", 1, 1, "Winter Is Coming")]
    [InlineData("The.Office.US.Season.2.Episode.5.avi", "The Office US", 2, 5, "")]
    [InlineData("Friends S10E17.mp4", "Friends", 10, 17, "")]
    [InlineData("Show.Name.S10E01.mkv", "Show Name", 10, 1, "")]
    [InlineData("S10E01.mkv", "", 10, 1, "")]
    [InlineData("My Show S10E01.mkv", "My Show", 10, 1, "")]
    public void ParseTvEpisode_ShouldExtractShowInfo(string fileName, string expectedShow, int expectedSeason, int expectedEpisode, string expectedTitle)
    {
        var (show, season, episode, title) = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(expectedShow, show);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedTitle, title);
    }
}
