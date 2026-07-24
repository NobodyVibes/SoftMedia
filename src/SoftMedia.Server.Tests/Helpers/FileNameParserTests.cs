using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// Movie- and TV-filename parsing tests for SR-WI-032 (year pattern order),
/// SR-WI-033 (episode-digit width, multi-episode files, anime bracket naming,
/// NxNN style) and SR-WI-038 (junk-word/release-tag title cleaning).
/// </summary>
public class FileNameParserTests
{
    // ---- SR-WI-032: parenthesized/bracketed year wins over a bare 4-digit ----

    [Theory]
    [InlineData("Blade Runner 2049 (2017).mkv", "Blade Runner 2049", 2017)]
    [InlineData("Wonder Woman 1984 (2020).mkv", "Wonder Woman 1984", 2020)]
    [InlineData("Death Race 2000 (1975).mkv", "Death Race 2000", 1975)]
    [InlineData("2001 A Space Odyssey (1968).mkv", "2001 A Space Odyssey", 1968)]
    [InlineData("1917 (2019).mkv", "1917", 2019)]
    // Bracketed year behaves like parenthesized
    [InlineData("Blade Runner 2049 [2017].mkv", "Blade Runner 2049", 2017)]
    // Dot-separated scene naming with a parenthesized year
    [InlineData("Blade.Runner.2049.(2017).1080p.mkv", "Blade Runner 2049", 2017)]
    public void ParseMovie_ParenthesizedYear_WinsOverBareNumber(string fileName, string expectedTitle, int? expectedYear)
    {
        var (title, year) = FileNameParser.ParseMovie(fileName);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // Pins EXISTING behavior: with no parenthesized year, a bare trailing 4-digit
    // number in plausible year range is still treated as the year (so bare
    // "Blade Runner 2049" reads as "Blade Runner" + 2049). SR-WI-032 must not
    // change this.
    [Theory]
    [InlineData("Blade Runner 2049.mkv", "Blade Runner", 2049)]
    [InlineData("Movie.Name.2019.1080p.BluRay.x264-SPARKS.mkv", "Movie Name", 2019)]
    [InlineData("The Matrix 1999.mkv", "The Matrix", 1999)]
    public void ParseMovie_BareYear_NoParens_KeepsExistingBehavior(string fileName, string expectedTitle, int? expectedYear)
    {
        var (title, year) = FileNameParser.ParseMovie(fileName);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // Pins EXISTING behavior for the "Year Title" pattern.
    [Fact]
    public void ParseMovie_LeadingYear_NoParens_KeepsExistingBehavior()
    {
        var (title, year) = FileNameParser.ParseMovie("2001 A Space Odyssey.mkv");
        Assert.Equal("A Space Odyssey", title);
        Assert.Equal(2001, year);
    }

    // ---- SR-WI-033: episode digit width (S01E100 must not become episode 0) ----

    [Theory]
    [InlineData("Show S01E100.mkv", "Show", 1, 100, "")]
    [InlineData("Show S02E123.mkv", "Show", 2, 123, "")]
    [InlineData("S01E100.mkv", "", 1, 100, "")]
    [InlineData("Long Running Show S12E345 Finale.mkv", "Long Running Show", 12, 345, "Finale")]
    public void ParseTvEpisode_ThreeDigitEpisodes_Parse(string fileName, string show, int season, int episode, string title)
    {
        var result = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
        Assert.Equal(title, result.EpisodeTitle);
    }

    // ---- SR-WI-033: multi-episode files parse as the primary episode ----
    // (The span end is consumed by the pattern but not returned — the result
    // tuple has no field for it.)

    [Theory]
    [InlineData("Show S01E01E02.mkv", "Show", 1, 1)]
    [InlineData("Show S01E01-E02.mkv", "Show", 1, 1)]
    [InlineData("Show S01E01-02.mkv", "Show", 1, 1)]
    [InlineData("Show.S03E10E11E12.mkv", "Show", 3, 10)]
    [InlineData("Show S02E100-E101.mkv", "Show", 2, 100)]
    public void ParseTvEpisode_MultiEpisode_ParsesPrimaryEpisode(string fileName, string show, int season, int episode)
    {
        var result = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
    }

    // ---- SR-WI-033: anime bracket naming (absolute numbering → season 1) ----

    [Theory]
    [InlineData("[SubsGroup] Show Name - 05 [1080p].mkv", "Show Name", 1, 5)]
    [InlineData("[SubsGroup][Raws] Show Name - 112 (BD 1080p).mkv", "Show Name", 1, 112)]
    [InlineData("[Group] Some Anime - 01.mkv", "Some Anime", 1, 1)]
    public void ParseTvEpisode_AnimeBracketNaming_ParsesEpisodeSeasonOne(string fileName, string show, int season, int episode)
    {
        var result = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
        Assert.Equal(string.Empty, result.EpisodeTitle); // quality tags never leak into title
    }

    // A leading [group] tag on a standard SxxExx name must not leak into the show name.
    [Fact]
    public void ParseTvEpisode_LeadingBracketGroup_StrippedFromShowName()
    {
        var result = FileNameParser.ParseTvEpisode("[HorribleSubs] Naruto S02E05.mkv");
        Assert.Equal("Naruto", result.ShowName);
        Assert.Equal(2, result.Season);
        Assert.Equal(5, result.Episode);
    }

    // ---- SR-WI-033: NxNN style (pre-existing support, pinned + widened episode) ----

    [Theory]
    [InlineData("Show 1x05.mkv", "Show", 1, 5, "")]
    [InlineData("Show 2x115 Something.mkv", "Show", 2, 115, "Something")]
    public void ParseTvEpisode_SeasonXEpisodeStyle_Parses(string fileName, string show, int season, int episode, string title)
    {
        var result = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
        Assert.Equal(title, result.EpisodeTitle);
    }

    // ---- Pins EXISTING TV behaviors that must survive the pattern changes ----

    [Theory]
    [InlineData("Show S01E05 Episode Title.mkv", "Show", 1, 5, "Episode Title")]
    [InlineData("Show Season 2 Episode 7.mkv", "Show", 2, 7, "")]
    [InlineData("Episode 3.mkv", "", 1, 3, "")]
    [InlineData("Part 2.mkv", "", 1, 2, "")]
    [InlineData("03 - The Third One.mkv", "", 1, 3, "The Third One")]
    public void ParseTvEpisode_ExistingPatterns_Unchanged(string fileName, string show, int season, int episode, string title)
    {
        var result = FileNameParser.ParseTvEpisode(fileName);
        Assert.Equal(show, result.ShowName);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
        Assert.Equal(title, result.EpisodeTitle);
    }

    // ---- SR-WI-038: junk/release-tag words are stripped from cleaned titles ----

    [Theory]
    // Release-group names newly added to the junk list
    [InlineData("Cool Movie REPACK YTS.mkv", "Cool Movie")]
    [InlineData("Heist Flick RARBG.mkv", "Heist Flick")]
    [InlineData("Another Movie EVO.mkv", "Another Movie")]
    [InlineData("Space Saga GalaxyTV.mkv", "Space Saga")]
    // Qualifiers newly added to the junk list
    [InlineData("Big Film REMUX.mkv", "Big Film")]
    [InlineData("Big Film IMAX Hybrid.mkv", "Big Film")]
    [InlineData("Big Film Unrated Extended.mkv", "Big Film")]
    [InlineData("Big Film AV1.mkv", "Big Film")]
    public void ParseMovie_JunkWords_StrippedFromTitle(string fileName, string expectedTitle)
    {
        var (title, _) = FileNameParser.ParseMovie(fileName);
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void ParseTvEpisode_JunkWords_StrippedFromEpisodeTitle()
    {
        var result = FileNameParser.ParseTvEpisode("Show.S01E01.720p.HDTV.x264-GALAXYTV.mkv");
        Assert.Equal("Show", result.ShowName);
        Assert.Equal(1, result.Season);
        Assert.Equal(1, result.Episode);
        Assert.Equal(string.Empty, result.EpisodeTitle);
    }
}
