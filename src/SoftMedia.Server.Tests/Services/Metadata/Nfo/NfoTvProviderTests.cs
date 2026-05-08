using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata.Nfo;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata.Nfo;

/// Wave D — discovery + dispatch behaviour for NfoTvProvider.
public class NfoTvProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IFileSystem _fs = new FileSystem();

    public NfoTvProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-nfo-tv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private NfoTvProvider NewProvider() => new(_fs, NullLogger<NfoTvProvider>.Instance);

    [Fact]
    public async Task FetchMetadataAsync_SeriesWithTvshowNfo_ReturnsParsedResult()
    {
        var seriesDir = Path.Combine(_tempDir, "MyShow");
        Directory.CreateDirectory(seriesDir);
        File.WriteAllText(Path.Combine(seriesDir, "tvshow.nfo"), """
            <tvshow>
                <title>My Show</title>
                <year>2020</year>
            </tvshow>
            """);

        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = seriesDir,
            Type = MediaType.Series,
            Title = "x", SortTitle = "x",
        });

        Assert.NotNull(result);
        Assert.Equal("My Show", result!.Title);
        Assert.Equal(2020, result.Year);
    }

    [Fact]
    public async Task FetchMetadataAsync_EpisodeWithStemNfo_ReturnsParsedResult()
    {
        var seriesDir = Path.Combine(_tempDir, "MyShow");
        Directory.CreateDirectory(seriesDir);
        var episodeFile = Path.Combine(seriesDir, "S01E03 - Trial.mkv");
        File.WriteAllText(episodeFile, "stub");
        File.WriteAllText(Path.Combine(seriesDir, "S01E03 - Trial.nfo"), """
            <episodedetails>
                <title>The Trial</title>
                <plot>An episode plot.</plot>
            </episodedetails>
            """);

        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = episodeFile,
            Type = MediaType.Episode,
            Title = "x", SortTitle = "x",
        });

        Assert.NotNull(result);
        Assert.Equal("The Trial", result!.Title);
        Assert.Contains("episode plot", result.Description);
    }

    [Fact]
    public async Task FetchMetadataAsync_EpisodeWithWrongRoot_ReturnsNull()
    {
        var seriesDir = Path.Combine(_tempDir, "Show");
        Directory.CreateDirectory(seriesDir);
        var episodeFile = Path.Combine(seriesDir, "S01E01.mkv");
        File.WriteAllText(episodeFile, "stub");
        // <movie> root for an episode = misfile.
        File.WriteAllText(Path.Combine(seriesDir, "S01E01.nfo"),
            "<movie><title>Wrong Tag</title></movie>");

        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = episodeFile,
            Type = MediaType.Episode,
            Title = "x", SortTitle = "x",
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_SeriesWithoutTvshowNfo_ReturnsNull()
    {
        var seriesDir = Path.Combine(_tempDir, "Empty");
        Directory.CreateDirectory(seriesDir);

        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = seriesDir,
            Type = MediaType.Series,
            Title = "x", SortTitle = "x",
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_UnsupportedType_ReturnsNull()
    {
        // Movie passed to TV provider — wrong library, refuse gracefully.
        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = Path.Combine(_tempDir, "movie.mkv"),
            Type = MediaType.Movie,
            Title = "x", SortTitle = "x",
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchMetadataAsync_SeasonType_ReturnsNull()
    {
        // v1 doesn't read season.nfo — out of scope per the plan.
        var seriesDir = Path.Combine(_tempDir, "Show");
        Directory.CreateDirectory(seriesDir);

        var result = await NewProvider().FetchMetadataAsync(new MediaItem
        {
            Id = Guid.NewGuid(),
            Path = seriesDir,
            Type = MediaType.Season,
            Title = "x", SortTitle = "x",
        });

        Assert.Null(result);
    }
}
