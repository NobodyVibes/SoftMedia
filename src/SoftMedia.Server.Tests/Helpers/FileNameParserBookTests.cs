using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// Real-world book filenames are chaotic — these cases are drawn from user libraries
/// where the naive "Author - Title" split was failing (e.g. series ordinals confused
/// with authors, parenthesized rip-tags taken as titles, Title-by-Author convention
/// read backwards). Each case proves the parser survives one specific pattern.
/// </summary>
public class FileNameParserBookTests
{
    [Theory]
    // Classic format — baseline that must keep working
    [InlineData("Stephen King - The Shining.epub",
                "Stephen King", "The Shining", null)]
    [InlineData("Jane Austen - Pride and Prejudice.epub",
                "Jane Austen", "Pride and Prejudice", null)]

    // Trailing (YYYY) year extraction
    [InlineData("Frank Herbert - Dune (1965).epub",
                "Frank Herbert", "Dune", 1965)]

    // Series-ordinal prefix "1 - Title - Author (YYYY)"
    [InlineData("1 - Dune - Frank Herbert (1965).epub",
                "Frank Herbert", "Dune", 1965)]
    [InlineData("2.5 - Red Plague - Kevin J. Anderson.epub",
                "Kevin J. Anderson", "Red Plague", null)]

    // "Title by Author" — English prose convention
    [InlineData("Dreamer of Dune by Brian Herbert.epub",
                "Brian Herbert", "Dreamer of Dune", null)]

    // "Lastname, Firstname - Title" — library-catalog convention
    [InlineData("King, Stephen - The Shining.epub",
                "Stephen King", "The Shining", null)]
    [InlineData("Manara, Milo - Click 2.pdf",
                "Milo Manara", "Click 2", null)]

    // Leading bracketed rip-tags get stripped off before parsing
    [InlineData("(Ebook) Stephen King - The Shining.epub",
                "Stephen King", "The Shining", null)]
    [InlineData("[Fiction] Jane Austen - Emma.epub",
                "Jane Austen", "Emma", null)]

    // Title itself contains " - "; author is at the end. The internal " - " is
    // normalized to whitespace by CleanName, which also collapses runs of spaces.
    [InlineData("The Sandworms - of Dune - Brian Herbert.epub",
                "Brian Herbert", "The Sandworms of Dune", null)]

    // Series-only layouts where no author is in the filename — title only
    [InlineData("Heroes of Dune 1 - Paul of Dune.epub",
                "",            "Paul of Dune", null)]
    public void ParseBook_ExtractsExpectedFields(
        string filename, string expectedAuthor, string expectedTitle, int? expectedYear)
    {
        var (author, title, year) = FileNameParser.ParseBook(filename);
        Assert.Equal(expectedAuthor, author);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Fact]
    public void ParseBook_EmptyInput_ReturnsEmpty()
    {
        var (author, title, year) = FileNameParser.ParseBook("");
        Assert.Equal(string.Empty, author);
        Assert.Equal(string.Empty, title);
        Assert.Null(year);
    }

    [Fact]
    public void ParseBook_AuthorOnlyFilename_FallsThroughToTitle()
    {
        // Degenerate: no title in the filename at all. OpenLibraryProvider has a
        // separate guard that drops the search when Title equals Director, so this
        // doesn't burn a metadata-retry slot.
        var (author, title, year) = FileNameParser.ParseBook("Stephen King.epub");
        Assert.Equal(string.Empty, author);
        Assert.Equal("Stephen King", title);
        Assert.Null(year);
    }

    [Fact]
    public void ParseBook_PathHint_BreaksAmbiguousNameTie_UsingParentDir()
    {
        // Regression: "Different Seasons - Stephen King.epub" has two name-shaped
        // segments; word counts tie (2 each). Without the path hint the parser
        // picked the first segment as author, inverting author/title. The parent
        // directory "Stephen King 121 Books Epub Collection 88" contains the
        // tokens "stephen" and "king", so the path-aware tie-break must prefer
        // the last segment as the author.
        var fullPath = Path.Combine(
            "Stephen King 121 Books Epub Collection 88",
            "Different Seasons - Stephen King.epub");

        var (author, title, year) = FileNameParser.ParseBook(fullPath);

        Assert.Equal("Stephen King", author);
        Assert.Equal("Different Seasons", title);
        Assert.Null(year);
    }

    [Fact]
    public void ParseBook_PathHint_LeavesUnambiguousCasesUnchanged()
    {
        // Even with a parent directory that could bias the tie-break, an
        // unambiguous Author-Title filename must still parse the classic way.
        var fullPath = Path.Combine(
            "Some Random Folder",
            "Frank Herbert - Dune (1965).epub");

        var (author, title, year) = FileNameParser.ParseBook(fullPath);

        Assert.Equal("Frank Herbert", author);
        Assert.Equal("Dune", title);
        Assert.Equal(1965, year);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Real-world filename patterns from C:\Users\Admin\Videos\book
    // Every case below is a file that was failing in production before the
    // Stephen-King-collection investigation. Keep these cases locked in —
    // they're the regression guard for the whole "25% failure rate" cluster.
    // ──────────────────────────────────────────────────────────────────────

    private const string KingFolder = "Stephen King 121 Books Epub Collection 88";

    [Theory]
    // Subtitle-padded title with strong parent-dir hint — previously picked
    // "Dolores Claiborne_ A Novel" as author because firstWords > lastWords.
    // CleanName collapses the underscore-dash into single spaces.
    [InlineData("Dolores Claiborne_ A Novel - Stephen King.epub",
                "Stephen King", "Dolores Claiborne A Novel", null)]
    // Co-author pattern — "and" between Joe Hill and Stephen King must be
    // recognised as a name, not confused with a title-word connector.
    [InlineData("Throttle - Joe Hill and Stephen King.epub",
                "Joe Hill and Stephen King", "Throttle", null)]
    [InlineData("Black House - Stephen King and Peter Straub.epub",
                "Stephen King and Peter Straub", "Black House", null)]
    [InlineData("The Talisman - Stephen King and Peter Straub.epub",
                "Stephen King and Peter Straub", "The Talisman", null)]
    // 4-digit year prefix — "1922" is a title, NOT a series ordinal. Ordinal
    // regex must not strip it.
    [InlineData("1922 - Stephen King.epub",
                "Stephen King", "1922", null)]
    // Date-style title "11-22-63" — hyphens attached to digits, no spaces.
    // Ordinal regex must not grab the leading "11-".
    [InlineData("11-22-63_A Novel - Stephen King.epub",
                "Stephen King", "11 22 63 A Novel", null)]
    // Classic two-name tie broken by parent-dir hint ("Stephen King" appears
    // in the folder). Single-word title that reads as a name ("Carrie").
    [InlineData("Carrie - Stephen King.epub",
                "Stephen King", "Carrie", null)]
    // "The" article prefix on first segment disables first-as-name; last
    // segment wins directly.
    [InlineData("The Dead Zone - Stephen King.epub",
                "Stephen King", "The Dead Zone", null)]
    public void ParseBook_RealKingCollectionFilenames(
        string filename, string expectedAuthor, string expectedTitle, int? expectedYear)
    {
        var fullPath = Path.Combine(KingFolder, filename);
        var (author, title, year) = FileNameParser.ParseBook(fullPath);
        Assert.Equal(expectedAuthor, author);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }
}
