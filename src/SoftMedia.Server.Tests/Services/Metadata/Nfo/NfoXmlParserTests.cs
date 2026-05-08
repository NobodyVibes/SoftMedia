using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata.Nfo;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata.Nfo;

/// Wave D — projection logic and security guards on NfoXmlParser.
public class NfoXmlParserTests : IDisposable
{
    private readonly string _tempDir;

    public NfoXmlParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-nfo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteNfo(string contents)
    {
        var path = Path.Combine(_tempDir, $"sample-{Guid.NewGuid():N}.nfo");
        File.WriteAllText(path, contents);
        return path;
    }

    private static IFileSystem RealFs() => new FileSystem();

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        var doc = NfoXmlParser.TryLoad(fs.Object, "/does/not/exist.nfo", NullLogger.Instance);

        Assert.Null(doc);
    }

    [Fact]
    public void TryLoad_WellFormedMovie_ReturnsDocument()
    {
        var path = WriteNfo("""
            <?xml version="1.0" encoding="UTF-8"?>
            <movie>
                <title>The Matrix</title>
                <year>1999</year>
            </movie>
            """);

        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);

        Assert.NotNull(doc);
        Assert.Equal("movie", doc!.Root!.Name.LocalName);
    }

    [Fact]
    public void TryLoad_DoctypeDeclaration_RejectedForXxeSafety()
    {
        var path = WriteNfo("""
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY x "leaked">]>
            <movie><title>&x;</title></movie>
            """);

        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);

        Assert.Null(doc);
    }

    [Fact]
    public void TryLoad_OversizeFile_Rejected()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        fs.Setup(f => f.GetFileLength(It.IsAny<string>())).Returns(2_000_000); // > 1 MiB cap

        var doc = NfoXmlParser.TryLoad(fs.Object, "/anything.nfo", NullLogger.Instance);

        Assert.Null(doc);
        // Stream was never opened — cap fires before IO.
        fs.Verify(f => f.OpenRead(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TryLoad_MalformedXml_LogsWarningAndReturnsNull()
    {
        var path = WriteNfo("<movie><title>broken");

        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);

        Assert.Null(doc);
    }

    [Fact]
    public void BuildFromRoot_PopulatesAllMappedFields()
    {
        var path = WriteNfo("""
            <?xml version="1.0"?>
            <movie>
                <title>Inception</title>
                <plot>A thief who steals corporate secrets through dream-sharing technology.</plot>
                <year>2010</year>
                <premiered>2010-07-16</premiered>
                <mpaa>PG-13</mpaa>
                <uniqueid type="imdb">tt1375666</uniqueid>
                <studio>Warner Bros</studio>
                <director>Christopher Nolan</director>
                <genre>Action</genre>
                <genre>Sci-Fi</genre>
                <rating>8.8</rating>
                <thumb>https://example.com/poster.jpg</thumb>
                <actor>
                    <name>Leonardo DiCaprio</name>
                    <role>Cobb</role>
                </actor>
            </movie>
            """);

        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        Assert.NotNull(doc);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.NotNull(result);
        Assert.Equal("Inception", result!.Title);
        Assert.Contains("dream-sharing", result.Description);
        Assert.Equal(2010, result.Year);
        Assert.Equal(2010, result.ReleaseDate?.Year);
        Assert.Equal("PG-13", result.ContentRating);
        Assert.Equal("tt1375666", result.ImdbId);
        Assert.Equal("Warner Bros", result.Studio);
        Assert.Equal("Christopher Nolan", result.Director);
        Assert.Equal(2, result.Genres?.Count);
        Assert.Contains("Action", result.Genres!);
        Assert.Contains("Sci-Fi", result.Genres!);
        Assert.Equal(8.8, result.Rating);
        Assert.Equal("https://example.com/poster.jpg", result.PosterUrl);
        Assert.Single(result.Cast!);
        Assert.Equal("Leonardo DiCaprio", result.Cast![0].Name);
        Assert.Equal("Cobb", result.Cast![0].Character);
    }

    [Fact]
    public void BuildFromRoot_PrefersPlotOverOutline()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <plot>The full plot.</plot>
                <outline>The outline.</outline>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Equal("The full plot.", result!.Description);
    }

    [Fact]
    public void BuildFromRoot_FallsBackToOutlineWhenPlotMissing()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <outline>The outline only.</outline>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Equal("The outline only.", result!.Description);
    }

    [Fact]
    public void BuildFromRoot_PrefersUniqueIdOverImdbId()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <uniqueid type="imdb">tt9999999</uniqueid>
                <imdbid>tt0000001</imdbid>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Equal("tt9999999", result!.ImdbId);
    }

    [Fact]
    public void BuildFromRoot_EmptyOrNAValuesIgnored()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <plot></plot>
                <studio>N/A</studio>
                <director>   </director>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Null(result!.Description);
        Assert.Null(result.Studio);
        Assert.Null(result.Director);
    }

    [Fact]
    public void BuildFromRoot_FirstDirectorWinsWhenMultiple()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <director>Joel Coen</director>
                <director>Ethan Coen</director>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Equal("Joel Coen", result!.Director);
    }

    [Fact]
    public void BuildFromRoot_RatingsBlockSupportsNewKodiFormat()
    {
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <ratings>
                    <rating name="imdb"><value>9.1</value></rating>
                    <rating name="tmdb"><value>8.0</value></rating>
                </ratings>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Equal(9.1, result!.Rating);
    }

    [Fact]
    public void BuildFromRoot_LocalFilePoster_NotAccepted()
    {
        // The v1 NFO provider only accepts http(s) poster URLs — local-file
        // <thumb> entries are out of scope per the plan.
        var path = WriteNfo("""
            <movie>
                <title>X</title>
                <thumb>poster-local.jpg</thumb>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Null(result!.PosterUrl);
    }

    [Fact]
    public void BuildFromRoot_NoUsableData_ReturnsNull()
    {
        var path = WriteNfo("""
            <movie>
                <plot></plot>
                <year>N/A</year>
            </movie>
            """);
        var doc = NfoXmlParser.TryLoad(RealFs(), path, NullLogger.Instance);
        var result = NfoXmlParser.BuildFromRoot(doc!.Root!);

        Assert.Null(result);
    }
}
