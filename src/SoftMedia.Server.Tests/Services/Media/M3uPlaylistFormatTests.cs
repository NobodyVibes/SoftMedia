using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Extended-M3U reading and writing. Pure string handling — no filesystem access,
/// which is what keeps an imported playlist from being able to probe the disk.
public class M3uPlaylistFormatTests
{
    // ── Writing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Write_EmitsHeaderNameAndEntries()
    {
        var content = M3uPlaylistFormat.Write("Road Trip", new[]
        {
            new M3uTrack("/music/a.flac", "Song A", "Band", 245),
        });

        Assert.StartsWith("#EXTM3U\n", content);
        Assert.Contains("#PLAYLIST:Road Trip\n", content);
        Assert.Contains("#EXTINF:245,Band - Song A\n", content);
        Assert.Contains("/music/a.flac\n", content);
    }

    [Fact]
    public void Write_OmitsTheArtistSeparatorWhenNoArtistIsKnown()
    {
        var content = M3uPlaylistFormat.Write("Mix", new[]
        {
            new M3uTrack("/music/a.flac", "Song A", null, 100),
        });

        Assert.Contains("#EXTINF:100,Song A\n", content);
        Assert.DoesNotContain(" - Song A", content);
    }

    [Fact]
    public void Write_UsesMinusOneForAnUnknownDuration()
    {
        var content = M3uPlaylistFormat.Write("Mix", new[]
        {
            new M3uTrack("/music/a.flac", "Song A", "Band", 0),
        });

        Assert.Contains("#EXTINF:-1,", content);
    }

    // A title containing a newline would otherwise close the #EXTINF line early
    // and let the rest be read as further playlist directives.
    [Fact]
    public void Write_NeutralisesNewlinesInsideMetadata()
    {
        var content = M3uPlaylistFormat.Write("Mix", new[]
        {
            new M3uTrack("/music/a.flac", "Evil\n#EXTINF:1,Injected", "Band", 10),
        });

        var lines = content.Split('\n');
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXTINF:1,Injected"));
    }

    [Fact]
    public void Write_HandlesAPlaylistWithNoTracks()
    {
        var content = M3uPlaylistFormat.Write("Empty", Array.Empty<M3uTrack>());

        Assert.Contains("#EXTM3U", content);
        Assert.Contains("#PLAYLIST:Empty", content);
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePaths_ReturnsOnlyPathLinesInOrder()
    {
        var paths = M3uPlaylistFormat.ParsePaths(
            "#EXTM3U\n#PLAYLIST:Mix\n#EXTINF:1,A\n/music/a.flac\n\n#EXTINF:2,B\n/music/b.flac\n");

        Assert.Equal(new[] { "/music/a.flac", "/music/b.flac" }, paths);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ParsePaths_AcceptsEveryLineEnding(string newline)
    {
        var content = string.Join(newline, "#EXTM3U", "/music/a.flac", "/music/b.flac");

        Assert.Equal(2, M3uPlaylistFormat.ParsePaths(content).Count);
    }

    // A deliberately repeated track should import as that same repetition.
    [Fact]
    public void ParsePaths_KeepsDuplicates()
    {
        var paths = M3uPlaylistFormat.ParsePaths("/music/a.flac\n/music/a.flac\n");

        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void ParsePaths_CapsTheNumberOfEntries()
    {
        var content = string.Join("\n", Enumerable.Range(0, M3uPlaylistFormat.MaxEntries + 500)
            .Select(i => $"/music/{i}.flac"));

        Assert.Equal(M3uPlaylistFormat.MaxEntries, M3uPlaylistFormat.ParsePaths(content).Count);
    }

    [Fact]
    public void ParsePaths_ReturnsNothingForEmptyInput()
    {
        Assert.Empty(M3uPlaylistFormat.ParsePaths(""));
        Assert.Empty(M3uPlaylistFormat.ParsePaths("   "));
        Assert.Empty(M3uPlaylistFormat.ParsePaths("#EXTM3U\n#EXTINF:1,Only comments\n"));
    }

    [Fact]
    public void ParseName_ReadsThePlaylistDirective()
    {
        Assert.Equal("Road Trip", M3uPlaylistFormat.ParseName("#EXTM3U\n#PLAYLIST:Road Trip\n/a.flac\n"));
    }

    [Fact]
    public void ParseName_IsNullWhenAbsentOrBlank()
    {
        Assert.Null(M3uPlaylistFormat.ParseName("#EXTM3U\n/a.flac\n"));
        Assert.Null(M3uPlaylistFormat.ParseName("#PLAYLIST:   \n"));
    }

    // ── Filename matching ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/music/artist/song.flac", "song.flac")]
    [InlineData("C:\\Music\\artist\\song.flac", "song.flac")]
    [InlineData("song.flac", "song.flac")]
    [InlineData("/music/trailing/", "trailing")]
    public void FileNameOf_TakesTheFinalSegmentOnEitherSeparator(string path, string expected)
    {
        Assert.Equal(expected, M3uPlaylistFormat.FileNameOf(path));
    }

    [Theory]
    [InlineData("/music/album/01.mp3", "album/01.mp3")]
    [InlineData("C:\\Music\\album\\01.mp3", "album/01.mp3")] // separators normalise
    [InlineData("album/01.mp3", "album/01.mp3")]
    [InlineData("01.mp3", "01.mp3")] // nothing to add
    public void TailOf_KeepsTheParentFolderSoSameNamedTracksStayDistinct(string path, string expected)
    {
        Assert.Equal(expected, M3uPlaylistFormat.TailOf(path));
    }

    // The whole point of the tail: "01.mp3" is not identifying on its own.
    [Fact]
    public void TailOf_DistinguishesTracksThatShareAFileName()
    {
        Assert.NotEqual(
            M3uPlaylistFormat.TailOf("/music/album-one/01.mp3"),
            M3uPlaylistFormat.TailOf("/music/album-two/01.mp3"));
    }

    // ── Round trip ───────────────────────────────────────────────────────────

    [Fact]
    public void WrittenPlaylistParsesBackToTheSamePaths()
    {
        var tracks = new[]
        {
            new M3uTrack("/music/a.flac", "A", "Band", 100),
            new M3uTrack("/music/b.flac", "B", null, 200),
        };

        var reparsed = M3uPlaylistFormat.ParsePaths(M3uPlaylistFormat.Write("Mix", tracks));

        Assert.Equal(tracks.Select(t => t.Path), reparsed);
    }

    [Fact]
    public void WrittenPlaylistKeepsItsNameThroughARoundTrip()
    {
        var content = M3uPlaylistFormat.Write("Café Sessions", Array.Empty<M3uTrack>());

        Assert.Equal("Café Sessions", M3uPlaylistFormat.ParseName(content));
    }
}
