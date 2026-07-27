using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// Embedded-metadata extraction is the first-choice path for book titles and
/// authors — whatever the publisher stamped into the OPF/Info dict is far
/// more reliable than filename heuristics. These tests assert the EPUB path
/// works on a minimal hand-built EPUB (ZIP + container.xml + content.opf) so
/// we don't need external test fixtures.
/// </summary>
public class BookMetadataExtractorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BookMetadataExtractor _sut;

    public BookMetadataExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-book-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _sut = new BookMetadataExtractor(NullLogger<BookMetadataExtractor>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExtractAsync_Epub_ReadsTitleAuthorYearPublisher()
    {
        var epubPath = Path.Combine(_tempDir, "sample.epub");
        BuildMinimalEpub(epubPath,
            title: "The Shining",
            author: "Stephen King",
            date: "1977-01-28",
            publisher: "Doubleday",
            description: "A family caretakes the Overlook Hotel.",
            isbn: "9780385121675",
            language: "en");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal("The Shining", result!.Title);
        Assert.Equal("Stephen King", result.Author);
        Assert.Equal(1977, result.Year);
        Assert.Equal("Doubleday", result.Publisher);
        Assert.Equal("A family caretakes the Overlook Hotel.", result.Description);
        Assert.Equal("9780385121675", result.Isbn);
        Assert.Equal("en", result.Language);
        Assert.True(result.HasUsableData);
    }

    [Fact]
    public async Task ExtractAsync_Epub_MissingOpf_ReturnsNull()
    {
        // EPUB with a valid ZIP structure but no container.xml — extractor must
        // fail gracefully so the scanner falls back to filename parsing.
        var epubPath = Path.Combine(_tempDir, "broken.epub");
        using (var zip = ZipFile.Open(epubPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("mimetype");
            using var s = entry.Open();
            var bytes = Encoding.ASCII.GetBytes("application/epub+zip");
            s.Write(bytes, 0, bytes.Length);
        }

        var result = await _sut.ExtractAsync(epubPath);
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedExtension_ReturnsNull()
    {
        var txtPath = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(txtPath, "not a book");

        var result = await _sut.ExtractAsync(txtPath);
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractAsync_NonexistentFile_ReturnsNull()
    {
        var result = await _sut.ExtractAsync(Path.Combine(_tempDir, "nope.epub"));
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractAsync_Epub_FlipsLastnameFirstnameCreator()
    {
        // Sigil/Calibre-ripped EPUBs commonly write <dc:creator>King, Stephen</dc:creator>
        // with no opf:file-as hint. Leaving the comma form in item.Director
        // sent OL queries as `author=King%2C+Stephen`, which returns zero docs.
        // The extractor must flip single-comma names to "Firstname Lastname".
        var epubPath = Path.Combine(_tempDir, "flipped-author.epub");
        BuildMinimalEpub(epubPath,
            title: "Dead Zone",
            author: "King, Stephen",
            date: "2016-01-01",
            publisher: "Scribner",
            description: "",
            isbn: "3864550337",
            language: "en");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal("Stephen King", result!.Author);
    }

    [Fact]
    public async Task ExtractAsync_Epub_PreservesNonInvertedAuthor()
    {
        // Regression guard: properly-formatted "Firstname Lastname" authors
        // must NOT be touched by the normalisation logic.
        var epubPath = Path.Combine(_tempDir, "normal-author.epub");
        BuildMinimalEpub(epubPath,
            title: "Dune", author: "Frank Herbert", date: "1965",
            publisher: "Chilton", description: "", isbn: "", language: "en");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal("Frank Herbert", result!.Author);
    }

    [Fact]
    public async Task ExtractAsync_Epub_MalformedOpf_ReturnsNull()
    {
        // Corrupt OPF XML must not crash the scan — extractor wraps the parse in
        // try/catch and hands back null so the filename parser takes over.
        var epubPath = Path.Combine(_tempDir, "malformed.epub");
        using (var zip = ZipFile.Open(epubPath, ZipArchiveMode.Create))
        {
            var containerEntry = zip.CreateEntry("META-INF/container.xml");
            using (var s = containerEntry.Open())
            {
                var xml = """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                    <rootfiles>
                        <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                    </rootfiles>
                </container>
                """;
                var bytes = Encoding.UTF8.GetBytes(xml);
                s.Write(bytes, 0, bytes.Length);
            }

            var opfEntry = zip.CreateEntry("OEBPS/content.opf");
            using (var s = opfEntry.Open())
            {
                var garbage = Encoding.UTF8.GetBytes("<this is not valid xml");
                s.Write(garbage, 0, garbage.Length);
            }
        }

        var result = await _sut.ExtractAsync(epubPath);
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────── page count (books)

    [Fact]
    public async Task ExtractAsync_Epub_ReadsEpub3NumberOfPages()
    {
        var epubPath = Path.Combine(_tempDir, "epub3-pages.epub");
        BuildMinimalEpub(epubPath,
            title: "It", author: "Stephen King", date: "1986", publisher: "Viking",
            description: "", isbn: "9780670813025", language: "en",
            extraMetadata: """<meta property="schema:numberOfPages">1138</meta>""");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal(1138, result!.PageCount);
    }

    [Fact]
    public async Task ExtractAsync_Epub_ReadsCalibrePageCount()
    {
        var epubPath = Path.Combine(_tempDir, "calibre-pages.epub");
        BuildMinimalEpub(epubPath,
            title: "Misery", author: "Stephen King", date: "1987", publisher: "Viking",
            description: "", isbn: "9780670813643", language: "en",
            extraMetadata: """<meta name="calibre:page_count" content="310"/>""");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal(310, result!.PageCount);
    }

    [Fact]
    public async Task ExtractAsync_Epub_WithoutPageDeclaration_LeavesPageCountNull()
    {
        // Reflowable text has no intrinsic pagination. Inventing a number here (spine
        // length, character count) would contradict every printed edition, so the field
        // must stay null and let the metadata provider supply the print figure.
        var epubPath = Path.Combine(_tempDir, "no-pages.epub");
        BuildMinimalEpub(epubPath,
            title: "Carrie", author: "Stephen King", date: "1974", publisher: "Doubleday",
            description: "", isbn: "9780385086950", language: "en");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Null(result!.PageCount);
    }

    [Theory]
    // Near-miss keys that are NOT counts — a substring match on "page" would swallow all three.
    [InlineData("""<meta property="rendition:spread">landscape</meta>""")]
    [InlineData("""<meta name="calibre:page_progression" content="ltr"/>""")]
    [InlineData("""<meta property="schema:numberOfPages">0</meta>""")]
    public async Task ExtractAsync_Epub_IgnoresNonCountPageMetadata(string extraMetadata)
    {
        var epubPath = Path.Combine(_tempDir, $"decoy-{Guid.NewGuid():N}.epub");
        BuildMinimalEpub(epubPath,
            title: "Cujo", author: "Stephen King", date: "1981", publisher: "Viking",
            description: "", isbn: "9780670456475", language: "en",
            extraMetadata: extraMetadata);

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Null(result!.PageCount);
    }

    [Fact]
    public async Task ExtractAsync_Epub_NormalizesUrnAndHyphenatedIsbn()
    {
        // Real OPFs write the identifier every which way; all forms must collapse to the
        // same digits so the file-wins precedence rule in MetadataAggregator compares like
        // with like against a provider's bare-digit ISBN.
        var epubPath = Path.Combine(_tempDir, "urn-isbn.epub");
        BuildMinimalEpub(epubPath,
            title: "The Stand", author: "Stephen King", date: "1978", publisher: "Doubleday",
            description: "", isbn: "urn:isbn:978-0-385-12168-2", language: "en");

        var result = await _sut.ExtractAsync(epubPath);

        Assert.NotNull(result);
        Assert.Equal("9780385121682", result!.Isbn);
    }

    [Fact]
    public async Task ExtractAsync_Pdf_ReadsPageCountEvenWithoutInfoDictionary()
    {
        // A PDF's page tree is authoritative and always present — scanner rips with an empty
        // Info dictionary used to return null wholesale and lose it. This is the exact case
        // that made "Pages" blank for PDF-only libraries.
        var pdfPath = Path.Combine(_tempDir, "three-pages.pdf");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        File.WriteAllBytes(pdfPath, builder.Build());

        var result = await _sut.ExtractAsync(pdfPath);

        Assert.NotNull(result);
        Assert.Equal(3, result!.PageCount);
    }

    // ────────────────────────────────────────────────────────────── helpers

    private static void BuildMinimalEpub(
        string path, string title, string author, string date,
        string publisher, string description, string isbn, string language,
        string extraMetadata = "")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        // mimetype (spec requires this to be first + stored, but EPUB readers in
        // the wild tolerate it being missing / deflated — our extractor doesn't care).
        AddEntry(zip, "mimetype", "application/epub+zip");

        AddEntry(zip, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                </rootfiles>
            </container>
            """);

        var opf = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="BookId">
                <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"
                          xmlns:opf="http://www.idpf.org/2007/opf">
                    <dc:title>{title}</dc:title>
                    <dc:creator opf:role="aut">{author}</dc:creator>
                    <dc:date>{date}</dc:date>
                    <dc:publisher>{publisher}</dc:publisher>
                    <dc:description>{description}</dc:description>
                    <dc:identifier id="BookId" opf:scheme="ISBN">{isbn}</dc:identifier>
                    <dc:language>{language}</dc:language>
                    {extraMetadata}
                </metadata>
            </package>
            """;
        AddEntry(zip, "OEBPS/content.opf", opf);
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
