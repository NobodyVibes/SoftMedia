using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

public class FileNameParserComicTests
{
    [Theory]
    // Explicit "Issue N"
    [InlineData("Amazing-Man Comics Issue 005.cbz",       "Amazing Man Comics", 5, null)]
    [InlineData("Mystery Men Comics Issue 012.cbz",       "Mystery Men Comics", 12, null)]
    // Hash issue
    [InlineData("The Spirit #7.cbz",                      "The Spirit", 7, null)]
    [InlineData("Captain Marvel Adventures #100.cbz",     "Captain Marvel Adventures", 100, null)]
    // Year captured
    [InlineData("Weird Fantasy Issue 013 (1952).cbz",     "Weird Fantasy", 13, 1952)]
    [InlineData("Action Comics #1 (1938).cbz",            "Action Comics", 1, 1938)]
    // Dash issue
    [InlineData("Detective Comics - 27.cbz",              "Detective Comics", 27, null)]
    // Volume prefix
    [InlineData("Batman v1 #1.cbz",                       "Batman", 1, null)]
    [InlineData("X-Men Vol 2 #25.cbz",                    "X Men", 25, null)]
    // Bare trailing number with year in parens
    [InlineData("Tales From the Crypt 22 (1951).cbz",     "Tales From the Crypt", 22, 1951)]
    public void ParseComic_ExtractsExpectedFields(string filename, string expectedSeries, int expectedIssue, int? expectedYear)
    {
        var (series, issue, year) = FileNameParser.ParseComic(filename);
        Assert.Equal(expectedSeries, series);
        Assert.Equal(expectedIssue, issue);
        Assert.Equal(expectedYear, year);
    }

    [Fact]
    public void ParseComic_NoIssueNumber_TreatsAsOneShot()
    {
        var (series, issue, year) = FileNameParser.ParseComic("Watchmen Special.cbz");
        Assert.Equal("Watchmen Special", series);
        Assert.Null(issue);
        Assert.Null(year);
    }

    [Fact]
    public void ParseComic_OneShotWithYear_KeepsYear()
    {
        var (series, issue, year) = FileNameParser.ParseComic("Watchmen Prequel (1986).cbz");
        Assert.Equal("Watchmen Prequel", series);
        Assert.Null(issue);
        Assert.Equal(1986, year);
    }

    [Fact]
    public void ParseComic_TrailingYearLikeNumber_NotMisreadAsIssue()
    {
        // "1977" looks issue-shaped but has no marker (#, Issue, dash, or year parens)
        // so it should fall through to the one-shot branch.
        var (series, issue, _) = FileNameParser.ParseComic("Star Wars 1977.cbz");
        Assert.Equal("Star Wars 1977", series);
        Assert.Null(issue);
    }

    [Fact]
    public void ParseComic_EmptyFilename_ReturnsEmpty()
    {
        var (series, issue, year) = FileNameParser.ParseComic("");
        Assert.Equal(string.Empty, series);
        Assert.Null(issue);
        Assert.Null(year);
    }
}
