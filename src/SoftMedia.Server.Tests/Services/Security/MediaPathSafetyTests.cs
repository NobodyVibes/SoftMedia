using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// Security audit H2/M2: paths that could break out of a quoted ffmpeg/ffprobe argument
/// must be flagged so they are never handed to a process. Legitimate media paths (spaces,
/// unicode, brackets, apostrophes) must pass.
public class MediaPathSafetyTests
{
    [Theory]
    [InlineData("/media/movies/Inception (2010).mkv")]
    [InlineData("/media/music/Sigur Rós - Untitled #3.flac")]
    [InlineData("/media/tv/It's Always Sunny - S01E01.mp4")]
    [InlineData("C:\\Media\\Show\\S01E01.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void Safe_paths_pass(string? path)
        => Assert.False(MediaPathSafety.HasArgumentInjectionRisk(path));

    [Fact]
    public void Quote_breakout_is_flagged()
        => Assert.True(MediaPathSafety.HasArgumentInjectionRisk("/media/movies/evil\" -map 0 -f rawvideo /tmp/x.mkv"));

    [Theory]
    [InlineData('\n')] // newline
    [InlineData('\r')] // carriage return
    [InlineData('\t')] // tab
    public void Control_characters_are_flagged(char control)
        => Assert.True(MediaPathSafety.HasArgumentInjectionRisk($"/media/movies/bad{control}name.mkv"));

    [Fact]
    public void Nul_character_is_flagged()
        => Assert.True(MediaPathSafety.HasArgumentInjectionRisk("/media/movies/bad" + (char)0 + "name.mkv"));
}
