using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class ComicInfoXmlProviderTests : IDisposable
{
    private readonly string _tempDir;

    public ComicInfoXmlProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cix_tests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ComicInfoXmlProvider NewProvider(IComicArchiveService svc) =>
        new(svc, NullLogger<ComicInfoXmlProvider>.Instance);

    [Theory]
    [InlineData(MediaType.Book)]
    [InlineData(MediaType.Movie)]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.Episode)]
    public async Task FetchMetadataAsync_NonComicTypes_ReturnsNull(MediaType type)
    {
        var archive = new Mock<IComicArchiveService>(MockBehavior.Strict);
        var item = new MediaItem { Type = type, Path = "does-not-matter.cbz" };

        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.Null(result);
        archive.VerifyNoOtherCalls(); // guard short-circuits before hitting the archive service
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicIssue_PopulatesFromXml()
    {
        var cbzPath = Path.Combine(_tempDir, "issue.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 }); // dummy zip header, never opened (mocked)

        var info = new ComicInfoXml
        {
            Title = "The Beginning",
            Series = "Amazing-Man Comics",
            Number = "5",
            Year = 1939,
            Month = 9,
            Publisher = "Centaur Publications",
            Writer = "Bill Everett, Ink Blotter",
            Genre = "Superhero, Action",
            Summary = "Issue summary.",
            PageCount = 12,
            AgeRating = "Everyone"
        };

        var archive = new Mock<IComicArchiveService>();
        archive.Setup(a => a.ExtractComicInfoAsync(cbzPath, It.IsAny<CancellationToken>()))
               .ReturnsAsync(info);

        var item = new MediaItem { Type = MediaType.ComicIssue, Path = cbzPath };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("The Beginning", result!.Title);
        Assert.Equal("Issue summary.", result.Description);
        Assert.Equal(1939, result.Year);
        Assert.Equal(new DateTime(1939, 9, 1, 0, 0, 0, DateTimeKind.Utc), result.ReleaseDate);
        Assert.Equal("Centaur Publications", result.Publisher);
        Assert.Equal("Centaur Publications", result.Studio);
        Assert.Equal("Bill Everett", result.Director); // First of comma-separated writers
        Assert.NotNull(result.Genres);
        Assert.Equal(new[] { "Superhero", "Action" }, result.Genres);
        Assert.Equal(12, result.PageCount);
        Assert.Equal("Everyone", result.ContentRating);
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicIssue_FallsBackToNumberWhenTitleMissing()
    {
        var cbzPath = Path.Combine(_tempDir, "notitle.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var archive = new Mock<IComicArchiveService>();
        archive.Setup(a => a.ExtractComicInfoAsync(cbzPath, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ComicInfoXml { Number = "12" });

        var item = new MediaItem { Type = MediaType.ComicIssue, Path = cbzPath };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("Issue #12", result!.Title);
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicSeries_UsesFirstCbzInFolder()
    {
        var seriesDir = Path.Combine(_tempDir, "series");
        Directory.CreateDirectory(seriesDir);
        var cbzA = Path.Combine(seriesDir, "A-first.cbz");
        var cbzB = Path.Combine(seriesDir, "B-second.cbz");
        File.WriteAllBytes(cbzA, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        File.WriteAllBytes(cbzB, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var archive = new Mock<IComicArchiveService>();
        archive.Setup(a => a.ExtractComicInfoAsync(cbzA, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ComicInfoXml
               {
                   Series = "Amazing-Man Comics",
                   Publisher = "Centaur Publications",
                   Summary = "Series summary.",
                   Year = 1939
               });

        var item = new MediaItem { Type = MediaType.ComicSeries, Path = seriesDir };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("Amazing-Man Comics", result!.Title);
        Assert.Equal("Series summary.", result.Description);
        Assert.Equal("Centaur Publications", result.Publisher);
        archive.Verify(a => a.ExtractComicInfoAsync(cbzA, It.IsAny<CancellationToken>()), Times.Once);
        archive.Verify(a => a.ExtractComicInfoAsync(cbzB, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicSeries_EmptyFolder_ReturnsNull()
    {
        var seriesDir = Path.Combine(_tempDir, "empty-series");
        Directory.CreateDirectory(seriesDir);

        var archive = new Mock<IComicArchiveService>(MockBehavior.Strict);

        var item = new MediaItem { Type = MediaType.ComicSeries, Path = seriesDir };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.Null(result);
        archive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FetchMetadataAsync_NoComicInfoXml_ReturnsNull()
    {
        var cbzPath = Path.Combine(_tempDir, "no-xml.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var archive = new Mock<IComicArchiveService>();
        archive.Setup(a => a.ExtractComicInfoAsync(cbzPath, It.IsAny<CancellationToken>()))
               .ReturnsAsync((ComicInfoXml?)null);

        var item = new MediaItem { Type = MediaType.ComicIssue, Path = cbzPath };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_ComicIssue_EmptyInfo_ReturnsNull()
    {
        var cbzPath = Path.Combine(_tempDir, "empty-info.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var archive = new Mock<IComicArchiveService>();
        archive.Setup(a => a.ExtractComicInfoAsync(cbzPath, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ComicInfoXml()); // All fields null

        var item = new MediaItem { Type = MediaType.ComicIssue, Path = cbzPath };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_MissingArchivePath_ReturnsNull()
    {
        var archive = new Mock<IComicArchiveService>(MockBehavior.Strict);

        var item = new MediaItem { Type = MediaType.ComicIssue, Path = Path.Combine(_tempDir, "nonexistent.cbz") };
        var result = await NewProvider(archive.Object).FetchMetadataAsync(item);

        Assert.Null(result);
        archive.VerifyNoOtherCalls();
    }
}
