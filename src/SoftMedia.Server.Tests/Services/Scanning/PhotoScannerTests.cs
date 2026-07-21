using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SkiaSharp;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class TestablePhotoScanner : PhotoScanner
{
    public TestablePhotoScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<PhotoScanner> logger,
        IMediaNotificationService notificationService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
    }

    public Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var fileDiscover = new FileDiscoveryResult(
            fileInfo.FullName,
            fileInfo.Exists ? fileInfo.Length : 0,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);
        return base.ProcessFileAsync(context, fileDiscover, existing, library, cancellationToken);
    }
}

public class PhotoScannerTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<ILogger<PhotoScanner>> _mockLogger = new();
    private readonly Mock<IMediaNotificationService> _mockNotification = new();
    private readonly Mock<IMetadataQueue> _mockQueue = new();
    private readonly AppDbContext _dbContext;
    private readonly string _tempDir;

    public PhotoScannerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-photoscanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private TestablePhotoScanner BuildScanner() => new(
        _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockQueue.Object);

    /// <summary>Writes a real decodable PNG so SKCodec dimension extraction runs.</summary>
    private string WritePng(string fileName, int width, int height)
    {
        var path = Path.Combine(_tempDir, fileName);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public async Task ProcessFileAsync_CreatesNewPhoto_WithDimensions_AndNoMetadataQueue()
    {
        var scanner = BuildScanner();
        var path = WritePng("Sunset at the lake.png", 32, 24);
        var library = new Library { Id = Guid.NewGuid(), Name = "Photos", Type = LibraryType.Photo };

        var result = await scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(ScanResult.New, result.Result);
        // Photos self-enrich inline — they must never hit the metadata queue.
        Assert.False(result.EnqueueMetadata);

        var photo = await _dbContext.MediaItems.SingleAsync();
        Assert.Equal(MediaType.Photo, photo.Type);
        Assert.Equal("Sunset at the lake", photo.Title);
        Assert.Equal(path, photo.Path);
        Assert.Equal(library.Id, photo.LibraryId);
        Assert.Equal(32, photo.Width);
        Assert.Equal(24, photo.Height);
        Assert.Equal("32x24", photo.Resolution);
        Assert.Equal("png", photo.Container);
        // Scan-time enrichment stamps the hash so MetadataEnrichmentPolicy is satisfied.
        Assert.False(string.IsNullOrEmpty(photo.MetadataHash));
    }

    [Fact]
    public async Task ProcessFileAsync_UnchangedFile_IsSkipped()
    {
        var scanner = BuildScanner();
        var path = WritePng("holiday.png", 8, 8);
        var library = new Library { Id = Guid.NewGuid(), Name = "Photos", Type = LibraryType.Photo };

        var first = await scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        Assert.Equal(ScanResult.New, first.Result);

        var existing = await _dbContext.MediaItems.SingleAsync();
        var second = await scanner.ProcessFileAsync(_dbContext, path, existing, library, CancellationToken.None);

        Assert.Equal(ScanResult.Skipped, second.Result);
    }

    [Fact]
    public async Task ProcessFileAsync_ChangedFile_IsUpdated_AndRereadsDimensions()
    {
        var scanner = BuildScanner();
        var path = WritePng("edited.png", 8, 8);
        var library = new Library { Id = Guid.NewGuid(), Name = "Photos", Type = LibraryType.Photo };

        await scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        var existing = await _dbContext.MediaItems.SingleAsync();

        // Replace with different dimensions; force a different mtime so the
        // size+mtime unchanged check cannot false-positive on fast filesystems.
        File.Delete(path);
        WritePng("edited.png", 16, 4);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var result = await scanner.ProcessFileAsync(_dbContext, path, existing, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(ScanResult.Updated, result.Result);
        var photo = await _dbContext.MediaItems.SingleAsync();
        Assert.Equal(16, photo.Width);
        Assert.Equal(4, photo.Height);
    }

    [Fact]
    public async Task ProcessFileAsync_ExiflessImage_LeavesExifJsonNull()
    {
        var scanner = BuildScanner();
        var path = WritePng("plain.png", 4, 4);
        var library = new Library { Id = Guid.NewGuid(), Name = "Photos", Type = LibraryType.Photo };

        await scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var photo = await _dbContext.MediaItems.SingleAsync();
        Assert.Null(photo.ExifJson);
    }

    [Fact]
    public void CanHandleFile_AcceptsPhotoExtensions_RejectsOthers()
    {
        var scanner = BuildScanner();
        Assert.True(scanner.CanHandleFile(Path.Combine(_tempDir, "a.jpg")));
        Assert.True(scanner.CanHandleFile(Path.Combine(_tempDir, "b.HEIC")));
        Assert.False(scanner.CanHandleFile(Path.Combine(_tempDir, "c.mkv")));
        Assert.False(scanner.CanHandleFile(Path.Combine(_tempDir, "d.mp3")));
    }

    [Fact]
    public void EnrichmentPolicy_ScannedPhoto_NeverNeedsEnrichment()
    {
        // Regression guard for the retry-loop hazard: a photo has no PosterUrl, so
        // without the Photo short-circuit the relaxed-mode `!hasPoster` check would
        // re-queue every photo forever.
        var scanned = new MediaItem { Type = MediaType.Photo, MetadataHash = "EXIF" };
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(scanned, strictMode: false));
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(scanned, strictMode: true));

        var unscanned = new MediaItem { Type = MediaType.Photo };
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(unscanned, strictMode: false));
    }
}
