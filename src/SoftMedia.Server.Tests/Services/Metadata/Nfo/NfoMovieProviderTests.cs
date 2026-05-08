using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata.Nfo;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata.Nfo;

/// Wave D — discovery + dispatch behaviour for NfoMovieProvider.
public class NfoMovieProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IFileSystem _fs = new FileSystem();

    public NfoMovieProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-nfo-movie-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private NfoMovieProvider NewProvider() => new(_fs, NullLogger<NfoMovieProvider>.Instance);

    private MediaItem MovieAt(string path) => new()
    {
        Id = Guid.NewGuid(),
        Path = path,
        Type = MediaType.Movie,
        Title = Path.GetFileNameWithoutExtension(path),
        SortTitle = Path.GetFileNameWithoutExtension(path),
    };

    [Fact]
    public async Task FetchMetadataAsync_StemNamedNfo_ReturnsParsedResult()
    {
        var movieFile = Path.Combine(_tempDir, "Inception (2010).mkv");
        File.WriteAllText(movieFile, "stub");
        File.WriteAllText(Path.Combine(_tempDir, "Inception (2010).nfo"), """
            <movie>
                <title>Inception</title>
                <year>2010</year>
            </movie>
            """);

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.NotNull(result);
        Assert.Equal("Inception", result!.Title);
        Assert.Equal(2010, result.Year);
    }

    [Fact]
    public async Task FetchMetadataAsync_FallsBackToMovieDotNfo()
    {
        var movieFile = Path.Combine(_tempDir, "movie-file.mkv");
        File.WriteAllText(movieFile, "stub");
        File.WriteAllText(Path.Combine(_tempDir, "movie.nfo"), """
            <movie><title>From Generic NFO</title></movie>
            """);

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.NotNull(result);
        Assert.Equal("From Generic NFO", result!.Title);
    }

    [Fact]
    public async Task FetchMetadataAsync_StemNfoTakesPriorityOverMovieNfo()
    {
        // Stem must differ from "movie" case-insensitively so the two NFO files
        // don't collide on Windows.
        var movieFile = Path.Combine(_tempDir, "Avatar.mkv");
        File.WriteAllText(movieFile, "stub");
        File.WriteAllText(Path.Combine(_tempDir, "Avatar.nfo"), "<movie><title>Stem Wins</title></movie>");
        File.WriteAllText(Path.Combine(_tempDir, "movie.nfo"), "<movie><title>Generic Loses</title></movie>");

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.Equal("Stem Wins", result!.Title);
    }

    [Fact]
    public async Task FetchMetadataAsync_NoNfoPresent_ReturnsNull()
    {
        var movieFile = Path.Combine(_tempDir, "Lonely.mkv");
        File.WriteAllText(movieFile, "stub");

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_WrongRootElement_ReturnsNull()
    {
        var movieFile = Path.Combine(_tempDir, "Wrong.mkv");
        File.WriteAllText(movieFile, "stub");
        // <episodedetails> in a movie folder = misfile.
        File.WriteAllText(Path.Combine(_tempDir, "Wrong.nfo"),
            "<episodedetails><title>Should Not Match</title></episodedetails>");

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_NonMovieMediaType_ReturnsNullWithoutTouchingDisk()
    {
        // Episode passed in by mistake — should refuse without touching the FS.
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = Path.Combine(_tempDir, "S01E01.mkv"),
            Type = MediaType.Episode,
            Title = "x", SortTitle = "x",
        };

        var result = await NewProvider().FetchMetadataAsync(item);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_EmptyPath_ReturnsNull()
    {
        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(), Path = "", Type = MediaType.Movie, Title = "x", SortTitle = "x"
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_MalformedNfo_ReturnsNull()
    {
        var movieFile = Path.Combine(_tempDir, "Broken.mkv");
        File.WriteAllText(movieFile, "stub");
        File.WriteAllText(Path.Combine(_tempDir, "Broken.nfo"), "<movie><title>broken");

        var result = await NewProvider().FetchMetadataAsync(MovieAt(movieFile));

        Assert.Null(result);
    }
}
