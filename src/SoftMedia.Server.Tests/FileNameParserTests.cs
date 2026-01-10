using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Tests;

public class FileNameParserTests
{
    [Theory]
    [InlineData(
        "The Hitchhikers Guide To The Galaxy - Remastered Mini Series 1981 1080p",
        "The Hitchhikers Guide To The Galaxy")]
    [InlineData(
        "Breaking Bad 2008 1080p BluRay",
        "Breaking Bad")]
    [InlineData(
        "Game of Thrones",
        "Game of Thrones")]
    [InlineData(
        "The Office US 2005",
        "The Office US")]
    [InlineData(
        "Fallout.2024.2160p.AMZN.WEB-DL",
        "Fallout")]
    [InlineData(
        "House.of.the.Dragon.S01.2160p.MAX.WEB-DL",
        "House of the Dragon S01")]
    [InlineData(
        "Band of Brothers - Complete Mini Series 2001 BluRay 1080p",
        "Band of Brothers")]
    public void CleanShowName_ShouldExtractCleanTitle(string input, string expected)
    {
        var result = FileNameParser.CleanShowName(input);
        Assert.Equal(expected, result);
    }
    
    [Fact]
    public void CleanShowName_ShouldHandleEmptyString()
    {
        var result = FileNameParser.CleanShowName("");
        Assert.Equal(string.Empty, result);
    }
    
    [Fact]
    public void CleanShowName_ShouldHandleNull()
    {
        var result = FileNameParser.CleanShowName(null!);
        Assert.Equal(string.Empty, result);
    }
    
    // === ParseTvEpisode Tests ===
    
    [Theory]
    [InlineData("Breaking.Bad.S01E01.Pilot", "Breaking Bad", 1, 1, "Pilot")]
    [InlineData("Breaking Bad S01E01 Pilot", "Breaking Bad", 1, 1, "Pilot")]
    [InlineData("The Office US S02E03", "The Office US", 2, 3, "")]
    [InlineData("Game.of.Thrones.S08E06.The.Iron.Throne", "Game of Thrones", 8, 6, "The Iron Throne")]
    public void ParseTvEpisode_ShouldParseStandardSxxExxFormat(string fileName, string expectedShow, int expectedSeason, int expectedEpisode, string expectedTitle)
    {
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode(fileName);
        
        Assert.Equal(expectedShow, showName);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedTitle, episodeTitle);
    }
    
    [Theory]
    [InlineData("Breaking Bad 1x01 Pilot", "Breaking Bad", 1, 1, "Pilot")]
    [InlineData("The.Office.2x03.The.Injury", "The Office", 2, 3, "The Injury")]
    [InlineData("Friends 5x12", "Friends", 5, 12, "")]
    public void ParseTvEpisode_ShouldParseSxExFormat(string fileName, string expectedShow, int expectedSeason, int expectedEpisode, string expectedTitle)
    {
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode(fileName);
        
        Assert.Equal(expectedShow, showName);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedTitle, episodeTitle);
    }
    
    // === Season 0 / Specials Tests ===
    
    [Theory]
    [InlineData("Breaking.Bad.S00E01.Pilot.Unaired", "Breaking Bad", 0, 1, "Pilot Unaired")]
    [InlineData("Game.of.Thrones.S00E02.Inside.the.Episode", "Game of Thrones", 0, 2, "Inside the Episode")]
    [InlineData("The Office S00E03 Behind the Scenes", "The Office", 0, 3, "Behind the Scenes")]
    public void ParseTvEpisode_ShouldParseSeason0Specials(string fileName, string expectedShow, int expectedSeason, int expectedEpisode, string expectedTitle)
    {
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode(fileName);
        
        Assert.Equal(expectedShow, showName);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedTitle, episodeTitle);
    }
    
    [Fact]
    public void ParseTvEpisode_S00E01_ShouldReturnSeasonZero()
    {
        // Explicit test for Season 0 Episode 1 - the most common special
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode("Show.Name.S00E01.Special.Episode");
        
        Assert.Equal("Show Name", showName);
        Assert.Equal(0, season);  // Critical: Season must be 0, not parsed as 00 -> 0 fails
        Assert.Equal(1, episode);
        Assert.Equal("Special Episode", episodeTitle);
    }
    
    // === Mini-series / Episode-Only Tests ===
    
    [Theory]
    [InlineData("Episode 01 The Beginning", 1, 1, "The Beginning")]
    [InlineData("E01 Pilot", 1, 1, "Pilot")]
    [InlineData("Part 1 Introduction", 1, 1, "Introduction")]
    [InlineData("01 First Episode", 1, 1, "First Episode")]
    public void ParseTvEpisode_ShouldParseMiniSeriesPatterns(string fileName, int expectedSeason, int expectedEpisode, string expectedTitle)
    {
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode(fileName);
        
        Assert.Equal(string.Empty, showName); // Show name comes from directory
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedTitle, episodeTitle);
    }
    
    [Fact]
    public void ParseTvEpisode_ShouldReturnEmptyForUnrecognizedFormat()
    {
        var (showName, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode("Random Movie Title 2024");
        
        Assert.Equal(string.Empty, showName);
        Assert.Equal(0, season);
        Assert.Equal(0, episode);
        Assert.Equal(string.Empty, episodeTitle);
    }
}
