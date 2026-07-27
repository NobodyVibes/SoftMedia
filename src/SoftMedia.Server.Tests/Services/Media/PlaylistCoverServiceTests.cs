using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// Playlist cover uploads. The properties under test are security ones: what
/// lands on disk is always our own re-encode, never the caller's bytes, and
/// nothing about the caller's filename or declared type is trusted.
/// </summary>
public class PlaylistCoverServiceTests : IDisposable
{
    private readonly string _root;
    private readonly PlaylistCoverService _service;

    public PlaylistCoverServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"softmedia-covers-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_root);

        _service = new PlaylistCoverService(env.Object, NullLogger<PlaylistCoverService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp dir */ }
    }

    private static MemoryStream MakePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }

    private string CoverPathOnDisk(Guid id)
        => Path.Combine(_root, "cache", "images", "playlists", $"{id}.webp");

    [Fact]
    public async Task Save_StoresTheCoverUnderThePlaylistId()
    {
        var id = Guid.NewGuid();
        using var png = MakePng(400, 400);

        var result = await _service.SaveAsync(id, png);

        Assert.True(result.Success);
        Assert.True(File.Exists(CoverPathOnDisk(id)));
        Assert.Contains($"/cache/images/playlists/{id}.webp", result.RelativePath);
    }

    // The stored bytes come from our encoder, so a file that is secretly something
    // else cannot be served back out of the media cache.
    [Fact]
    public async Task Save_ReEncodesToWebPRatherThanKeepingTheUploadedBytes()
    {
        var id = Guid.NewGuid();
        using var png = MakePng(300, 300);

        await _service.SaveAsync(id, png);

        var written = await File.ReadAllBytesAsync(CoverPathOnDisk(id));
        // RIFF....WEBP — the uploaded PNG's \x89PNG signature must be gone.
        Assert.Equal(new byte[] { 0x52, 0x49, 0x46, 0x46 }, written.Take(4));
        Assert.Equal("WEBP"u8.ToArray(), written.Skip(8).Take(4));
    }

    [Fact]
    public async Task Save_RejectsBytesThatAreNotAnImage()
    {
        var id = Guid.NewGuid();
        using var notAnImage = new MemoryStream("<html><script>alert(1)</script></html>"u8.ToArray());

        var result = await _service.SaveAsync(id, notAnImage);

        Assert.False(result.Success);
        Assert.False(File.Exists(CoverPathOnDisk(id)));
    }

    [Fact]
    public async Task Save_RejectsAnEmptyUpload()
    {
        var result = await _service.SaveAsync(Guid.NewGuid(), new MemoryStream());

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Save_RejectsAnUploadOverTheSizeCap()
    {
        var id = Guid.NewGuid();
        // Incompressible noise, so the cap is hit by real bytes rather than by an
        // image that happens to encode large.
        var oversized = new byte[PlaylistCoverService.MaxUploadBytes + 1024];
        Random.Shared.NextBytes(oversized);

        var result = await _service.SaveAsync(id, new MemoryStream(oversized));

        Assert.False(result.Success);
        Assert.False(File.Exists(CoverPathOnDisk(id)));
    }

    // Covers render in square tiles everywhere; cropping once here spares every
    // consumer from letterboxing.
    [Fact]
    public async Task Save_CropsToASquare()
    {
        var id = Guid.NewGuid();
        using var wide = MakePng(800, 400);

        await _service.SaveAsync(id, wide);

        using var stored = SKBitmap.Decode(CoverPathOnDisk(id));
        Assert.Equal(stored.Width, stored.Height);
    }

    [Fact]
    public async Task Save_BoundsTheStoredDimensions()
    {
        var id = Guid.NewGuid();
        using var huge = MakePng(3000, 3000);

        await _service.SaveAsync(id, huge);

        using var stored = SKBitmap.Decode(CoverPathOnDisk(id));
        Assert.True(stored.Width <= 1000, $"stored width was {stored.Width}");
    }

    [Fact]
    public async Task Save_ReplacesAPreviousCover()
    {
        var id = Guid.NewGuid();
        using var first = MakePng(200, 200);
        using var second = MakePng(600, 600);

        await _service.SaveAsync(id, first);
        var firstSize = new FileInfo(CoverPathOnDisk(id)).Length;
        await _service.SaveAsync(id, second);
        var secondSize = new FileInfo(CoverPathOnDisk(id)).Length;

        Assert.NotEqual(firstSize, secondSize);
        // One cover per playlist: the id IS the filename, so no orphan accumulates.
        var files = Directory.GetFiles(Path.Combine(_root, "cache", "images", "playlists"), $"{id}*");
        Assert.Single(files);
    }

    [Fact]
    public async Task Save_ReturnsACacheBustingPath()
    {
        var id = Guid.NewGuid();
        using var png = MakePng(200, 200);

        var result = await _service.SaveAsync(id, png);

        // The filename never changes, so without a version marker the browser
        // would keep showing the previous cover.
        Assert.Contains("?v=", result.RelativePath);
    }

    [Fact]
    public async Task Delete_RemovesTheStoredFile()
    {
        var id = Guid.NewGuid();
        using var png = MakePng(200, 200);
        await _service.SaveAsync(id, png);

        _service.Delete(id);

        Assert.False(File.Exists(CoverPathOnDisk(id)));
    }

    [Fact]
    public void Delete_IsSafeWhenThereIsNoCover()
    {
        _service.Delete(Guid.NewGuid()); // must not throw
    }
}
