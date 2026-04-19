using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// End-to-end tests that run <see cref="ComicInfoXmlProvider"/> against the actual
/// generated fixture CBZs (test-fixtures/books/cbz). This verifies the full round-trip:
///   scripts/generate-test-books.ps1 → real CBZ with ComicInfo.xml
///   → ComicArchiveService.ExtractComicInfoAsync (real ZipArchive)
///   → ComicInfoXmlProvider.FetchMetadataAsync
/// with no mocks, catching drift between fixture generation and parsing.
///
/// Skipped gracefully if the fixtures haven't been generated yet (e.g. on CI where
/// the PowerShell generator hasn't run).
/// </summary>
public class ComicInfoXmlProviderIntegrationTests
{
    private static string? FixturePath(string fileName)
    {
        // Walk up from bin/Debug/net8.0 to the repo root to locate test-fixtures.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "test-fixtures", "books", "cbz", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static ComicInfoXmlProvider NewProvider()
    {
        var archiveSvc = new ComicArchiveService(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 }),
            NullLogger<ComicArchiveService>.Instance);
        return new ComicInfoXmlProvider(archiveSvc, NullLogger<ComicInfoXmlProvider>.Instance);
    }

    [Fact]
    public async Task AmazingManComics_FixturePopulatesIssueMetadata()
    {
        var path = FixturePath("Amazing-Man Comics Issue 005.cbz");
        if (path is null)
        {
            // Fixtures not generated (CI environment). Skip silently rather than fail.
            return;
        }

        var provider = NewProvider();
        var item = new MediaItem
        {
            Type = MediaType.ComicIssue,
            Path = path,
            Title = "Issue #5" // stub — provider should override from XML
        };

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("The Beginning", result!.Title);
        Assert.Equal(1939, result.Year);
        Assert.Equal("Centaur Publications", result.Publisher);
        Assert.Equal("Centaur Publications", result.Studio);
        Assert.Equal("Bill Everett", result.Director);
        Assert.NotNull(result.Genres);
        Assert.Contains("Superhero", result.Genres!);
        Assert.Contains("Action", result.Genres);
        Assert.Equal(12, result.PageCount);
        Assert.Contains("mystic adventurer", result.Description);
    }

    [Fact]
    public async Task MysteryMenComics_FixturePopulatesIssueMetadata()
    {
        var path = FixturePath("Mystery Men Comics Issue 012.cbz");
        if (path is null) return;

        var provider = NewProvider();
        var item = new MediaItem { Type = MediaType.ComicIssue, Path = path };

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("The Blue Beetle Returns", result!.Title);
        Assert.Equal(1940, result.Year);
        Assert.Equal("Fox Feature Syndicate", result.Publisher);
        Assert.Equal("Will Eisner", result.Director);
        Assert.Equal(25, result.PageCount);
    }

    [Fact]
    public async Task WeirdFantasy_FixturePopulatesIssueMetadata()
    {
        var path = FixturePath("Weird Fantasy Issue 013.cbz");
        if (path is null) return;

        var provider = NewProvider();
        var item = new MediaItem { Type = MediaType.ComicIssue, Path = path };

        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("The Last Page", result!.Title);
        Assert.Equal(1952, result.Year);
        Assert.Equal("EC Comics", result.Publisher);
        Assert.Equal("Al Feldstein", result.Director);
        Assert.NotNull(result.Genres);
        Assert.Contains("Science Fiction", result.Genres!);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public async Task SeriesLookup_ReadsFirstIssueInFolder()
    {
        var firstIssue = FixturePath("Amazing-Man Comics Issue 005.cbz");
        if (firstIssue is null) return;

        var seriesDir = Path.GetDirectoryName(firstIssue)!;

        var provider = NewProvider();
        var seriesItem = new MediaItem { Type = MediaType.ComicSeries, Path = seriesDir };

        var result = await provider.FetchMetadataAsync(seriesItem);

        // The fixture folder has three different series. Series resolver picks the
        // first CBZ alphabetically — "Amazing-Man Comics Issue 005.cbz" — so we
        // expect its series-level fields.
        Assert.NotNull(result);
        Assert.Equal("Amazing-Man Comics", result!.Title);
        Assert.Equal("Centaur Publications", result.Publisher);
    }
}
