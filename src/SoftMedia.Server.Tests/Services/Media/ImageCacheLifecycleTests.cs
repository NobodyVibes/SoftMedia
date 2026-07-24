using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// SR-WI-037 — image cache lifecycle: orphan cleanup (row-existence criterion, books
/// directory included) and provider-artwork invalidation for the per-item metadata
/// refresh (local-sidecar copies retained).
public class ImageCacheLifecycleTests : IDisposable
{
    private readonly string _webRoot;
    private readonly string _cacheRoot;
    private readonly ImageCacheService _service;

    public ImageCacheLifecycleTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "sm-imglc-" + Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(_webRoot);
        _cacheRoot = Path.Combine(_webRoot, "cache", "images");

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);
        // No network activity in these tests; the HttpClient is never exercised.
        _service = new ImageCacheService(new HttpClient(), NullLogger<ImageCacheService>.Instance,
            env.Object, Mock.Of<SoftMedia.Server.Services.Abstractions.IStreamSecurityService>());
    }

    public void Dispose()
    {
        try { Directory.Delete(Directory.GetParent(_webRoot)!.FullName, true); } catch { }
    }

    private string Touch(string subDir, string fileName)
    {
        var path = Path.Combine(_cacheRoot, subDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    // ---------------------------------------------------------------- CleanupOrphanedImages

    [Fact]
    public void Cleanup_DeletesArtForIdWithNoRow_RetainsArtForKnownIds()
    {
        var liveId = Guid.NewGuid();
        var missingButPresentId = Guid.NewGuid(); // IsMissing row: still a DB row → still valid
        var orphanId = Guid.NewGuid();            // no DB row at all → orphan

        var liveFile = Touch("movies", $"{liveId}_poster.jpg");
        var missingFile = Touch("movies", $"{missingButPresentId}_poster.jpg");
        var orphanFile = Touch("movies", $"{orphanId}_poster.jpg");

        // The contract is row-existence: callers pass ALL MediaItems ids, including
        // soft-deleted (IsMissing) rows, whose artwork must survive the sweep.
        var deleted = _service.CleanupOrphanedImages(new HashSet<Guid> { liveId, missingButPresentId });

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(liveFile));
        Assert.True(File.Exists(missingFile));
        Assert.False(File.Exists(orphanFile));
    }

    [Fact]
    public void Cleanup_CoversBooksDirectory()
    {
        var keptBookId = Guid.NewGuid();
        var orphanBookId = Guid.NewGuid();

        var kept = Touch("books", $"{keptBookId}_poster.jpg");
        var orphan = Touch("books", $"{orphanBookId}_poster.jpg");

        var deleted = _service.CleanupOrphanedImages(new HashSet<Guid> { keptBookId });

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void Cleanup_SweepsEveryMediaKeyedDirectory_ButNotCast()
    {
        var orphan = Guid.NewGuid();
        var tv = Touch("tv", $"{orphan}_poster.jpg");
        var movies = Touch("movies", $"{orphan}_poster.jpg");
        var music = Touch("music", $"{orphan}_cover.jpg");
        var games = Touch("games", $"{orphan}_poster.jpg");
        var books = Touch("books", $"{orphan}_poster.jpg");
        // tv/cast is keyed by int person ids, cleaned via DeleteCastImagesForPersonIds —
        // the orphan sweep must never touch it (its names would parse as "invalid format").
        var cast = Touch(Path.Combine("tv", "cast"), "42.jpg");

        var deleted = _service.CleanupOrphanedImages(new HashSet<Guid>());

        Assert.Equal(5, deleted);
        Assert.False(File.Exists(tv));
        Assert.False(File.Exists(movies));
        Assert.False(File.Exists(music));
        Assert.False(File.Exists(games));
        Assert.False(File.Exists(books));
        Assert.True(File.Exists(cast));
    }

    // ------------------------------------------------------------ InvalidateCachedImagesAsync

    [Fact]
    public async Task Invalidate_RemovesTheItemsProviderFiles_LeavesOtherItems()
    {
        var target = Guid.NewGuid();
        var bystander = Guid.NewGuid();

        var poster = Touch("movies", $"{target}_poster.jpg");
        var other = Touch("movies", $"{bystander}_poster.jpg");

        var deleted = await _service.InvalidateCachedImagesAsync(target);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(poster));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Invalidate_SeriesId_DropsSeasonPostersAndStillsToo()
    {
        var seriesId = Guid.NewGuid();
        // Season posters and episode stills are keyed by the SERIES id, so a series-level
        // refresh invalidates the whole family in one call.
        var poster = Touch("tv", $"{seriesId}_poster.jpg");
        var season = Touch("tv", $"{seriesId}_season01_poster.jpg");
        var still = Touch("tv", $"{seriesId}_s01e02_still.jpg");

        var deleted = await _service.InvalidateCachedImagesAsync(seriesId);

        Assert.Equal(3, deleted);
        Assert.False(File.Exists(poster));
        Assert.False(File.Exists(season));
        Assert.False(File.Exists(still));
    }

    [Fact]
    public async Task Invalidate_RetainsLocalSidecarCopies()
    {
        var id = Guid.NewGuid();
        var provider = Touch("movies", $"{id}_poster.jpg");
        // R-WI-014 local key: not provider-refreshable — deleting it would leave the DB's
        // /cache URL dangling until the next scan re-ingests the sidecar.
        var local = Touch("movies", $"{id}_poster_local.jpg");

        var deleted = await _service.InvalidateCachedImagesAsync(id);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(provider));
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task Invalidate_UnknownId_DeletesNothing()
    {
        var existing = Touch("books", $"{Guid.NewGuid()}_poster.jpg");

        var deleted = await _service.InvalidateCachedImagesAsync(Guid.NewGuid());

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(existing));
    }
}
