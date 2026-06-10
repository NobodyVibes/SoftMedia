using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

public class ComicArchiveServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ComicArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia_cbz_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateCbz(string name, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_tempDir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(data, 0, data.Length);
        }
        return path;
    }

    private static ComicArchiveService NewService() =>
        new(new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 }), NullLogger<ComicArchiveService>.Instance);

    [Fact]
    public async Task ExtractComicInfo_OversizedXml_IsRejectedNotParsed_L4()
    {
        // ComicInfo.xml whose decompressed text far exceeds the 1 MiB cap — a small-compressed
        // entry that inflates large. The capped XmlReader must abort rather than build the tree.
        var hugeXml = "<ComicInfo><Notes>" + new string('a', 1_100_000) + "</Notes></ComicInfo>";
        var path = CreateCbz("bomb.cbz",
            ("page1.jpg", new byte[] { 1, 2, 3 }),
            ("ComicInfo.xml", System.Text.Encoding.UTF8.GetBytes(hugeXml)));

        var info = await NewService().ExtractComicInfoAsync(path);

        Assert.Null(info); // over-cap XML is treated as absent metadata, never fully materialised
    }

    [Fact]
    public void IsSupportedArchive_ReturnsTrueForCbzAndCbr()
    {
        var svc = NewService();
        Assert.True(svc.IsSupportedArchive("book.cbz"));
        Assert.True(svc.IsSupportedArchive("BOOK.CBZ"));
        Assert.True(svc.IsSupportedArchive("book.cbr"));
        Assert.True(svc.IsSupportedArchive("BOOK.CBR"));
        Assert.False(svc.IsSupportedArchive("book.pdf"));
        Assert.False(svc.IsSupportedArchive("book.epub"));
    }

    [Fact]
    public async Task GetPageCountAsync_CountsOnlyImageEntries()
    {
        var path = CreateCbz("pages.cbz",
            ("page1.jpg", new byte[] { 1 }),
            ("page2.png", new byte[] { 2 }),
            ("cover.webp", new byte[] { 3 }),
            ("ComicInfo.xml", new byte[] { 4 }),
            ("notes.txt", new byte[] { 5 }));

        var svc = NewService();
        var count = await svc.GetPageCountAsync(path);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetPageAsync_ReturnsPagesInNaturalOrder()
    {
        // Intentionally add in mixed order; natural sort must still produce page1 < page2 < page10.
        var path = CreateCbz("order.cbz",
            ("page10.png", new byte[] { 10 }),
            ("page2.png", new byte[] { 2 }),
            ("page1.png", new byte[] { 1 }));

        var svc = NewService();

        var p1 = await svc.GetPageAsync(path, 1);
        var p2 = await svc.GetPageAsync(path, 2);
        var p3 = await svc.GetPageAsync(path, 3);

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.NotNull(p3);
        Assert.Equal(1, p1!.Data[0]);
        Assert.Equal(2, p2!.Data[0]);
        Assert.Equal(10, p3!.Data[0]);
        Assert.Equal("image/png", p1.ContentType);
    }

    [Fact]
    public async Task GetPageAsync_ReturnsNullForOutOfRange()
    {
        var path = CreateCbz("short.cbz", ("p1.jpg", new byte[] { 1 }));
        var svc = NewService();

        Assert.Null(await svc.GetPageAsync(path, 99));
        Assert.Null(await svc.GetPageAsync(path, 0));
        Assert.Null(await svc.GetPageAsync(path, -5));
    }

    [Fact]
    public async Task GetPageAsync_ThrowsForUnsupportedFormat()
    {
        var pdfPath = Path.Combine(_tempDir, "book.pdf");
        await File.WriteAllBytesAsync(pdfPath, new byte[] { 1, 2, 3 });

        var svc = NewService();
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.GetPageAsync(pdfPath, 1));
    }

    // ──────────────────────────────────────────────────────────── CBR dispatch

    // The RAR format is proprietary-write — SharpCompress (and every managed
    // alternative) reads RAR but cannot produce one. Verifying CBR support in
    // code is therefore limited to (a) extension acceptance, (b) dispatch to
    // the RAR code path, and (c) graceful failure on malformed bytes. The
    // happy-path test below is marked Skip and requests a real .cbr fixture
    // be dropped into TestData/ by a human.

    [Fact]
    public async Task GetPageCountAsync_MalformedCbrSurfacesException()
    {
        // Non-RAR bytes under a .cbr extension. Previously this would have been
        // rejected at IsSupportedArchive; post-ER-001, the extension is accepted
        // and SharpCompress raises a format exception when parsing begins.
        var path = Path.Combine(_tempDir, "garbage.cbr");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        var svc = NewService();

        // Don't pin to a specific SharpCompress exception type — it has changed
        // between versions (InvalidFormatException vs InvalidOperationException).
        // We only care that the failure is raised, not swallowed, so the controller
        // can translate it to a clean 500.
        await Assert.ThrowsAnyAsync<Exception>(() => svc.GetPageCountAsync(path));
    }

    [Fact]
    public async Task ExtractComicInfoAsync_MalformedCbrReturnsNull()
    {
        // Contract mirrors the malformed-CBZ case: ComicInfo extraction must
        // never throw — callers treat it as optional metadata.
        var path = Path.Combine(_tempDir, "garbage_info.cbr");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00, 0x11, 0x22, 0x33 });

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.Null(info);
    }

    [Fact]
    public void IsSupportedArchive_AcceptsCbrAfterExtensionWidening()
    {
        // Regression guard for the scanner — BookScanner's ComicExtensions set
        // and this service's IsSupportedArchive must stay in sync, otherwise
        // files picked up by the scan would fail at read time.
        var svc = NewService();
        Assert.True(svc.IsSupportedArchive("/lib/comics/issue1.cbr"));
        Assert.True(svc.IsSupportedArchive("/lib/comics/issue1.CBR"));
    }

    [Fact(Skip = "Requires a real .cbr fixture in TestData/. RAR archives cannot be "
                 + "synthesised from code; drop a tiny 2-image .cbr at "
                 + "src/SoftMedia.Server.Tests/TestData/sample.cbr and enable this test.")]
    public async Task GetPageAsync_RealCbrFixtureReturnsPagesInOrder()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.cbr");
        Assert.True(File.Exists(fixturePath), $"Fixture missing: {fixturePath}");

        var svc = NewService();
        var count = await svc.GetPageCountAsync(fixturePath);
        Assert.True(count > 0);

        var first = await svc.GetPageAsync(fixturePath, 1);
        Assert.NotNull(first);
        Assert.StartsWith("image/", first!.ContentType);
    }

    // ─────────────────────────────────────────────────── ComicInfo.xml extraction

    private const string SampleComicInfoXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ComicInfo xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <Title>The Beginning</Title>
  <Series>Amazing-Man Comics</Series>
  <Number>5</Number>
  <Volume>1</Volume>
  <Year>1939</Year>
  <Month>9</Month>
  <Publisher>Centaur Publications</Publisher>
  <Writer>Bill Everett</Writer>
  <Penciller>Bill Everett</Penciller>
  <Summary>Bill Everett's mystic adventurer debuts.</Summary>
  <Genre>Superhero, Action</Genre>
  <PageCount>12</PageCount>
</ComicInfo>";

    [Fact]
    public async Task ExtractComicInfoAsync_ReadsEmbeddedXml()
    {
        var path = CreateCbz("with-info.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(SampleComicInfoXml)),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.NotNull(info);
        Assert.Equal("The Beginning", info!.Title);
        Assert.Equal("Amazing-Man Comics", info.Series);
        Assert.Equal("5", info.Number);
        Assert.Equal(1, info.Volume);
        Assert.Equal(1939, info.Year);
        Assert.Equal(9, info.Month);
        Assert.Equal("Centaur Publications", info.Publisher);
        Assert.Equal("Bill Everett", info.Writer);
        Assert.Equal("Bill Everett", info.Penciller);
        Assert.Equal("Superhero, Action", info.Genre);
        Assert.Equal(12, info.PageCount);
        Assert.Contains("mystic adventurer", info.Summary);
    }

    [Fact]
    public async Task ExtractComicInfoAsync_ReturnsNullWhenAbsent()
    {
        var path = CreateCbz("no-info.cbz",
            ("page1.jpg", new byte[] { 1 }),
            ("page2.jpg", new byte[] { 2 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.Null(info);
    }

    [Fact]
    public async Task ExtractComicInfoAsync_TolleratesLowercaseFilename()
    {
        var path = CreateCbz("lower.cbz",
            ("comicinfo.xml", Encoding.UTF8.GetBytes(SampleComicInfoXml)),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.NotNull(info);
        Assert.Equal("Amazing-Man Comics", info!.Series);
    }

    [Fact]
    public async Task ExtractComicInfoAsync_HandlesMalformedXml()
    {
        var path = CreateCbz("malformed.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><unclosed>")),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.Null(info); // Malformed XML treated as absent, not thrown
    }

    [Fact]
    public async Task ExtractComicInfoAsync_IgnoresWrongRootElement()
    {
        var notComicInfo = @"<?xml version=""1.0""?><SomethingElse><Title>X</Title></SomethingElse>";
        var path = CreateCbz("wrongroot.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(notComicInfo)),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.Null(info);
    }

    [Fact]
    public async Task ExtractComicInfoAsync_CachesResultKeyedByMtime()
    {
        var path = CreateCbz("cached.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(SampleComicInfoXml)),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var first = await svc.ExtractComicInfoAsync(path);
        var second = await svc.ExtractComicInfoAsync(path);

        // Same mtime → cache hit returns the same instance.
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task ExtractComicInfoAsync_PartialFieldsReturnPartialObject()
    {
        var partial = @"<?xml version=""1.0""?><ComicInfo><Series>Weird Fantasy</Series><Number>13</Number></ComicInfo>";
        var path = CreateCbz("partial.cbz",
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(partial)),
            ("page1.jpg", new byte[] { 1 }));

        var svc = NewService();
        var info = await svc.ExtractComicInfoAsync(path);

        Assert.NotNull(info);
        Assert.Equal("Weird Fantasy", info!.Series);
        Assert.Equal("13", info.Number);
        Assert.Null(info.Title);
        Assert.Null(info.Publisher);
        Assert.Null(info.Year);
    }

    [Fact]
    public async Task GetPageAsync_AssignsCorrectContentType()
    {
        var path = CreateCbz("types.cbz",
            ("a.jpg", new byte[] { 1 }),
            ("b.png", new byte[] { 2 }),
            ("c.webp", new byte[] { 3 }),
            ("d.gif", new byte[] { 4 }));

        var svc = NewService();

        Assert.Equal("image/jpeg", (await svc.GetPageAsync(path, 1))!.ContentType);
        Assert.Equal("image/png", (await svc.GetPageAsync(path, 2))!.ContentType);
        Assert.Equal("image/webp", (await svc.GetPageAsync(path, 3))!.ContentType);
        Assert.Equal("image/gif", (await svc.GetPageAsync(path, 4))!.ContentType);
    }
}
