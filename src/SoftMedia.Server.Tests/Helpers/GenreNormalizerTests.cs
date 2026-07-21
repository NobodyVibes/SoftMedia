using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// Genre canonicalisation. Every malformed input below is a real value taken from
/// the Genres table, where 47 of 349 rows were BISAC subject headings stored
/// verbatim and "Science Fiction" existed three times under different casing.
public class GenreNormalizerTests
{
    [Theory]
    [InlineData("Comedy", "Comedy")]
    [InlineData("comedy", "Comedy")]
    [InlineData("COMEDY", "Comedy")]
    [InlineData("  Comedy  ", "Comedy")]
    [InlineData("heavy metal", "Heavy Metal")]
    [InlineData("melodic death metal", "Melodic Death Metal")]
    public void CanonicalisesCasingAndWhitespace(string raw, string expected)
    {
        Assert.Equal(new[] { expected }, GenreNormalizer.Normalize(raw));
    }

    [Fact]
    public void CollapsesTheThreeScienceFictionSpellingsToOne()
    {
        var all = GenreNormalizer.NormalizeAll(
            new[] { "Science Fiction", "Science fiction", "science fiction" });
        Assert.Equal(new[] { "Science Fiction" }, all);
    }

    [Theory]
    // BISAC path headings — the dominant malformed shape from book providers.
    [InlineData("FICTION / Science Fiction / Space Opera",
        new[] { "Fiction", "Science Fiction", "Space Opera" })]
    [InlineData("FICTION / Science Fiction / Action & Adventure",
        new[] { "Fiction", "Science Fiction", "Action & Adventure" })]
    [InlineData("FICTION / General", new[] { "Fiction", "General" })]
    public void SplitsBisacPathsOnTheSpacedSlash(string raw, string[] expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(raw));
    }

    [Theory]
    // A BARE slash is part of the genre name in music tagging. Splitting these
    // produced the nonsense genres "Melodic" and "Death-Metal" against real data.
    [InlineData("pop/rock", "Pop/Rock")]
    [InlineData("melodic/death-metal", "Melodic/Death-Metal")]
    public void DoesNotSplitBareSlashesUsedInsideMusicGenreNames(string raw, string expected)
    {
        Assert.Equal(new[] { expected }, GenreNormalizer.Normalize(raw));
    }

    [Theory]
    // Comma splitting is deliberately NOT done — book subject headings are
    // comma-joined and indistinguishable from genre lists. "Herbert, frank,
    // 1920-1986" is an author; splitting it invented the genres "Herbert" and
    // "Frank". These rows stay intact (ugly) rather than becoming junk (wrong).
    [InlineData("Herbert, frank, 1920-1986")]
    [InlineData("Serial murders, fiction")]
    [InlineData("Fiction, science fiction, general")]
    public void LeavesCommaJoinedSubjectHeadingsIntactRatherThanInventingGenres(string raw)
    {
        var result = GenreNormalizer.Normalize(raw).ToList();
        Assert.Single(result);
        Assert.DoesNotContain("Herbert", result);
        Assert.DoesNotContain("Frank", result);
    }

    [Fact]
    public void DropsParentheticalSubjectHeadings()
    {
        // "Dune (Imaginary place)" is a setting, not a genre — nothing usable.
        Assert.Empty(GenreNormalizer.Normalize("Dune (Imaginary place)"));
        Assert.Empty(GenreNormalizer.Normalize("Roland (Fictitious character : King)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1984")]          // a year, not a genre
    [InlineData("(fictitious character)")]
    public void DropsUnusableSegments(string raw)
    {
        Assert.Empty(GenreNormalizer.Normalize(raw));
    }

    [Fact]
    public void DropsOverlongSegmentsThatSurvivedSplitting()
    {
        var essay = new string('a', 60);
        Assert.Empty(GenreNormalizer.Normalize(essay));
    }

    [Fact]
    public void PreservesHyphensAndDeliberateInnerCasing()
    {
        // Splitting must not treat '-' as a separator, and ToTitleCase must not
        // mangle styling that the provider chose on purpose.
        Assert.Equal(new[] { "Sci-Fi" }, GenreNormalizer.Normalize("sci-fi"));
        Assert.Equal(new[] { "iTunes Exclusive" }, GenreNormalizer.Normalize("iTunes Exclusive"));
    }

    [Fact]
    public void SplitsSpacedSlashWithoutBreakingAmpersandGenres()
    {
        Assert.Equal(new[] { "R&B", "Soul" }, GenreNormalizer.Normalize("R&B / Soul"));
        Assert.Equal(new[] { "Rock & Roll" }, GenreNormalizer.Normalize("rock & roll"));
    }

    [Fact]
    public void DeduplicatesAcrossEntriesPreservingFirstSeenOrder()
    {
        var all = GenreNormalizer.NormalizeAll(
            new[] { "Adventure", "FICTION / Adventure", "comedy", "Comedy" });
        Assert.Equal(new[] { "Adventure", "Fiction", "Comedy" }, all);
    }

    [Fact]
    public void CapsGenresPerItemSoASubjectDumpCannotExplodeTheList()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"Genre{i}").ToArray();
        var all = GenreNormalizer.NormalizeAll(many);
        Assert.Equal(GenreNormalizer.MaxGenresPerItem, all.Count);
    }

    [Fact]
    public void HandlesNullAndEmptyInputWithoutThrowing()
    {
        Assert.Empty(GenreNormalizer.Normalize(null));
        Assert.Empty(GenreNormalizer.NormalizeAll(null));
        Assert.Empty(GenreNormalizer.NormalizeAll(new string?[] { null, "", "   " }));
    }
}
