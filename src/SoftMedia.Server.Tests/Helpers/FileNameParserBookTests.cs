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
}
