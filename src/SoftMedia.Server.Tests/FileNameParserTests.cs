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
}
