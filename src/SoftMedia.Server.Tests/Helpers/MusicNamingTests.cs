using System.IO;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// Multi-disc albums split into one album per disc when the Album tag carries a
/// disc suffix ("Covers (CD1)") — these lock in the normalization that collapses
/// them, AND guard against false-merging genuinely separate releases.
public class MusicNamingTests
{
    [Theory]
    // Bracketed "(CDn)" suffix (real titles from the affected library)
    [InlineData("Covers (CD1)", "Covers", 1)]
    [InlineData("Covers (CD2)", "Covers", 2)]
    [InlineData("Anthrology: No Hit Wonders (1985-1991) (CD1)", "Anthrology: No Hit Wonders (1985-1991)", 1)]
    [InlineData("Tyrants Of The Rising Sun: Live In Japan (CD2)", "Tyrants Of The Rising Sun: Live In Japan", 2)]
    [InlineData("Only (CD1)", "Only", 1)]
    // Dashed "- CD n[: subtitle]" suffix (note the subtitle may contain parens)
    [InlineData("War Eternal (Limited Deluxe Artbook Edition) - CD 1", "War Eternal (Limited Deluxe Artbook Edition)", 1)]
    [InlineData("War Eternal (Limited Deluxe Artbook Edition) - CD 2: Seeds of War (The Demos)", "War Eternal (Limited Deluxe Artbook Edition)", 2)]
    [InlineData("War Eternal (Limited Deluxe Artbook Edition) - CD 3: Instrumental Play-Through", "War Eternal (Limited Deluxe Artbook Edition)", 3)]
    // Other common encodings
    [InlineData("The Wall [Disc 2]", "The Wall", 2)]
    [InlineData("Mellon Collie, Disc 1", "Mellon Collie", 1)]
    [InlineData("Some Album Disc 3", "Some Album", 3)]
    [InlineData("Some Album CD2", "Some Album", 2)]
    [InlineData("Greatest Hits (Disk 1)", "Greatest Hits", 1)]
    public void NormalizeAlbumName_StripsDiscSuffix(string input, string expectedName, int expectedDisc)
    {
        var (name, disc) = MusicNaming.NormalizeAlbumName(input);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedDisc, disc);
    }

    [Theory]
    // Genuinely separate releases / titles that merely resemble a disc marker —
    // must be left intact with no disc number.
    [InlineData("Volume 8 - The Threat Is Real")]   // "Volume" is not a disc marker
    [InlineData("Use Your Illusion II")]            // roman numeral, not "CD 2"
    [InlineData("The Greater Of Two Evils (Digipack) (Bonus CD)")] // "CD" with no number
    [InlineData("We've Come For You All (Digipack) (Bonus CD)")]
    [InlineData("Revolution Begins")]
    [InlineData("Disintegration")]                  // contains "Dis" but not "disc<n>"
    [InlineData("1989")]
    [InlineData("Greatest Hits")]
    [InlineData("MixCD1")]                          // marker glued to a word — no boundary
    [InlineData("Sign o' the Times")]
    [InlineData("CD1")]                             // whole title is the marker — keep as-is
    [InlineData("")]
    public void NormalizeAlbumName_LeavesNonDiscTitlesUnchanged(string input)
    {
        var (name, disc) = MusicNaming.NormalizeAlbumName(input);
        Assert.Equal(input, name);
        Assert.Null(disc);
    }

    [Fact]
    public void ParseDiscNumberFromPath_ReadsDiscSubfolder()
    {
        Assert.Equal(1, MusicNaming.ParseDiscNumberFromPath(Path.Combine("Music", "Album", "CD1", "01.mp3")));
        Assert.Equal(2, MusicNaming.ParseDiscNumberFromPath(Path.Combine("Music", "Album", "Disc 2", "x.flac")));
        Assert.Equal(3, MusicNaming.ParseDiscNumberFromPath(Path.Combine("Music", "Album", "CD 03", "x.mp3")));
        Assert.Null(MusicNaming.ParseDiscNumberFromPath(Path.Combine("Music", "Album", "01.mp3")));
        Assert.Null(MusicNaming.ParseDiscNumberFromPath(Path.Combine("Music", "Covers (2 CD)", "x.mp3")));
    }

    [Fact]
    public void GetAlbumDirectory_UsesParentForDiscSubfolders()
    {
        var releaseDir = Path.Combine("Music", "Anthrax", "2008 - Covers (2 CD)");
        var trackInDisc = Path.Combine(releaseDir, "CD1", "01.mp3");
        Assert.Equal(releaseDir, MusicNaming.GetAlbumDirectory(trackInDisc));

        var flatDir = Path.Combine("Music", "Anthrax", "Persistence Of Time");
        var trackFlat = Path.Combine(flatDir, "01.mp3");
        Assert.Equal(flatDir, MusicNaming.GetAlbumDirectory(trackFlat));
    }
}
