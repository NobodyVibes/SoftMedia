using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Metadata.Nfo;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// R-WI-014 — local artwork sidecars. Pins the three load-bearing behaviours:
/// (1) discovery precedence + application with the local flag,
/// (2) THE ENRICHMENT TRAP: a local poster must NOT satisfy Relaxed completeness until one
///     enrichment pass has stamped MetadataHash — else poster.jpg movies never get descriptions,
///     and after that pass they must be complete — else they'd re-enrich forever,
/// (3) sidecar removal clears local art so provider art can return.
public class LocalArtworkTests : IDisposable
{
    private readonly string _mediaDir;
    private readonly Mock<IImageCacheService> _cache = new();
    private readonly LocalArtworkService _svc;

    public LocalArtworkTests()
    {
        _mediaDir = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mediaDir);
        _cache.Setup(c => c.CacheLocalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string src, string key, string _) => $"/cache/images/{key}{Path.GetExtension(src)}");
        _svc = new LocalArtworkService(_cache.Object, NullLogger<LocalArtworkService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_mediaDir, recursive: true); } catch { /* best effort */ }
    }

    private MediaItem Movie() => new() { Id = Guid.NewGuid(), Title = "M", Type = MediaType.Movie, Path = Path.Combine(_mediaDir, "m.mkv") };

    // ---- Discovery + application ----

    [Fact]
    public async Task PosterJpg_IsApplied_WithLocalFlag_AndSourceDistinctKey()
    {
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        var item = Movie();

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        Assert.True(result.Changed);
        // "_poster_local" — NEVER the provider's "_poster" key (review: shared keys let a
        // provider download shadow the user's sidecar).
        Assert.Equal($"/cache/images/movies/{item.Id}_poster_local.jpg", item.PosterUrl);
        Assert.True(item.PosterFromLocalFile);
        _cache.Verify(c => c.CacheLocalImageAsync(
            Path.Combine(_mediaDir, "poster.jpg"), $"movies/{item.Id}_poster_local", _mediaDir), Times.Once);
    }

    [Fact]
    public async Task ProvidedDirectoryListing_IsUsed_InsteadOfLiveGetFiles()
    {
        // SM-WI-051 — scanners pass their per-scan listing memo so a flat folder is
        // listed once per scan, not once per media file. Proof of the seam: a poster
        // exists ON DISK, but the provided listing omits it → no poster applied, and
        // the service never fell back to its own Directory.GetFiles.
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        var item = Movie();
        var listingCalls = 0;

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m", dir =>
        {
            listingCalls++;
            return Array.Empty<string>(); // deliberately hides the on-disk poster
        });

        Assert.Equal(1, listingCalls);
        Assert.False(result.Changed);
        Assert.Null(item.PosterUrl);
        _cache.Verify(c => c.CacheLocalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StemPoster_Beats_FolderLevelNames()
    {
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m-poster.png"), new byte[] { 1 });
        var item = Movie();

        await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        _cache.Verify(c => c.CacheLocalImageAsync(
            Path.Combine(_mediaDir, "m-poster.png"), It.IsAny<string>(), It.IsAny<string>()), Times.Once); // most specific wins
    }

    [Fact]
    public async Task FanartAndBackdrop_ApplyToBackdropUrl()
    {
        File.WriteAllBytes(Path.Combine(_mediaDir, "fanart.jpg"), new byte[] { 1 });
        var item = Movie();

        await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        Assert.Equal($"/cache/images/movies/{item.Id}_backdrop_local.jpg", item.BackdropUrl);
        Assert.True(item.BackdropFromLocalFile);
    }

    [Fact]
    public async Task LockedItems_AreNeverTouched()
    {
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        var item = Movie();
        item.MetadataLocked = true;

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        Assert.False(result.Changed);
        Assert.Null(item.PosterUrl);
        _cache.Verify(c => c.CacheLocalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemovedSidecar_ClearsLocalArt_DeletesCacheCopy_AndSignalsReenrichment()
    {
        var item = Movie();
        item.PosterUrl = $"/cache/images/movies/{item.Id}_poster_local.jpg"; // sidecar-owned
        item.PosterFromLocalFile = true;

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m"); // folder has no sidecars

        Assert.True(result.Changed);
        Assert.True(result.LocalPosterRemoved);
        Assert.Null(item.PosterUrl);              // poster-less again → provider art can return
        Assert.False(item.PosterFromLocalFile);
        _cache.Verify(c => c.DeleteCachedLocalImage($"movies/{item.Id}_poster_local"), Times.Once); // no stale resurrection
    }

    [Fact]
    public async Task NfoSourcedLocalPoster_IsNeverClearedByTheSidecarSweep()
    {
        // Review HIGH: the sweep can't see NFO-referenced files (cover.jpg, extras/poster.png)
        // and used to clear them every scan, causing a permanent clear→re-enrich→re-ingest
        // cycle. Only "_poster_local" (sweep-owned) art may be cleared.
        var item = Movie();
        item.PosterUrl = $"/cache/images/movies/{item.Id}_poster_nfo.png"; // NFO-ingested
        item.PosterFromLocalFile = true;

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m"); // no sidecars present

        Assert.False(result.Changed);
        Assert.False(result.LocalPosterRemoved);
        Assert.Equal($"/cache/images/movies/{item.Id}_poster_nfo.png", item.PosterUrl); // untouched
        Assert.True(item.PosterFromLocalFile);
    }

    [Fact]
    public async Task FlatMultiMovieFolder_IgnoresBarePosterNames_ButHonorsStemPoster()
    {
        // Review MEDIUM: in a flat folder with several movies, poster.jpg belongs to no one —
        // applying it to every movie would override each one's correct provider art.
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m-poster.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m.mkv"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "other.mkv"), new byte[] { 1 }); // second movie → shared folder

        var item = Movie();
        await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");
        _cache.Verify(c => c.CacheLocalImageAsync(
            Path.Combine(_mediaDir, "m-poster.jpg"), It.IsAny<string>(), It.IsAny<string>()), Times.Once); // per-file name still applies

        var other = Movie();
        var result = await _svc.ApplyLocalArtworkAsync(other, _mediaDir, "other");
        Assert.False(result.Changed); // bare poster.jpg NOT applied to "other" in a shared folder
    }

    [Fact]
    public async Task TrailerAndSampleClips_DoNotMakeAFolderShared()
    {
        // Verifier finding: Radarr/Kodi layouts keep Movie-trailer.mkv beside the movie —
        // counting companions as "other movies" wrongly disabled the bare poster.jpg.
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m.mkv"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m-trailer.mkv"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_mediaDir, "m-sample.mkv"), new byte[] { 1 });

        var item = Movie();
        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        Assert.True(result.Changed); // still a dedicated folder → bare poster applies
        Assert.True(item.PosterFromLocalFile);
    }

    [Fact]
    public async Task NonMovieTvTypes_AreIgnored()
    {
        File.WriteAllBytes(Path.Combine(_mediaDir, "poster.jpg"), new byte[] { 1 });
        var item = Movie();
        item.Type = MediaType.Book;

        var result = await _svc.ApplyLocalArtworkAsync(item, _mediaDir, "m");

        Assert.False(result.Changed);
    }

    // ---- The enrichment trap (spec's CRITICAL constraint) ----

    [Fact]
    public void LocalPoster_DoesNotSatisfyCompleteness_UntilOneEnrichmentPassRan()
    {
        var item = new MediaItem
        {
            Type = MediaType.Movie,
            PosterUrl = "/cache/images/movies/x_poster.jpg",
            PosterFromLocalFile = true, // local art, never enriched (no hash)
        };

        // Relaxed mode would normally declare any postered item complete — the local flag
        // must keep it enrichable so the movie still receives a description.
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));

        // …and after ONE pass (hash stamped) it must be complete, or it would re-enrich forever.
        item.MetadataHash = "stamped";
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));
    }

    [Fact]
    public void RemotePoster_Completeness_Unchanged()
    {
        var item = new MediaItem { Type = MediaType.Movie, PosterUrl = "/cache/images/movies/x_poster.jpg" };
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false)); // provider poster = complete (pre-existing)

        var bare = new MediaItem { Type = MediaType.Movie };
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(bare, strictMode: false));
    }

    [Fact]
    public void StrictMode_LocalPosterCountsAsPoster_ButDescriptionStillRequired()
    {
        var item = new MediaItem
        {
            Type = MediaType.Movie,
            PosterUrl = "/cache/images/movies/x_poster.jpg",
            PosterFromLocalFile = true,
            MetadataHash = "stamped",
        };
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));  // no description yet

        item.Overview = "a plot";
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    // ---- NFO local thumb safety ----

    [Theory]
    [InlineData("poster.jpg", true)]
    [InlineData("extras/poster.png", true)]
    [InlineData("../outside.jpg", false)]      // traversal
    [InlineData("C:\\evil\\x.jpg", false)]     // rooted
    [InlineData("\\\\server\\share\\x.jpg", false)] // UNC
    [InlineData("poster.exe", false)]          // not an image
    [InlineData("x:alternate.jpg", false)]     // drive/ADS colon
    public void NfoLocalThumb_OnlySafeRelativeImagePathsAccepted(string value, bool expected)
        => Assert.Equal(expected, NfoXmlParser.IsSafeRelativeImagePath(value));
}
