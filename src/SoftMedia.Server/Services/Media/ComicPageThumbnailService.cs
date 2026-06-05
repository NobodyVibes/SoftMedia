using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Memory-cached per-page thumbnail producer for comic archives. Extracts the
/// raw page image via <see cref="IComicArchiveService"/>, downsamples with
/// SkiaSharp, and re-encodes as JPEG (small files, near-universal browser
/// support). Entries are keyed by <c>(archive path, mtime, page, width)</c>
/// so stale entries evict automatically when an archive is replaced.
/// </summary>
public class ComicPageThumbnailService : IComicPageThumbnailService
{
    // 30-min sliding expiration matches the page-byte cache in
    // ComicArchiveService. Size=1 so the shared MemoryCache SizeLimit caps the
    // total entry count rather than a byte budget — each thumbnail is ~10–40
    // KB and pages vary, so LRU-by-count is the sane policy.
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(30),
        Size = 1,
    };

    // JPEG quality trade: 70 is small enough for scrubber previews without
    // visible artefacts at 160px-wide and below.
    private const int JpegQuality = 70;

    private readonly IComicArchiveService _archive;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ComicPageThumbnailService> _logger;

    public ComicPageThumbnailService(
        IComicArchiveService archive,
        IMemoryCache cache,
        ILogger<ComicPageThumbnailService> logger)
    {
        _archive = archive;
        _cache = cache;
        _logger = logger;
    }

    public async Task<byte[]?> GetAsync(
        string archivePath,
        int pageNumber,
        int width,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || width < 16 || width > 1024) return null;

        var mtimeTicks = File.GetLastWriteTimeUtc(archivePath).Ticks;
        var key = $"comic:thumb:{archivePath}:{mtimeTicks}:{pageNumber}:{width}";

        if (_cache.TryGetValue(key, out byte[]? cached) && cached is not null)
        {
            return cached;
        }

        var page = await _archive.GetPageAsync(archivePath, pageNumber, cancellationToken);
        if (page is null) return null;

        byte[] bytes;
        try
        {
            bytes = ResizeToJpeg(page.Data, width);
        }
        catch (Exception ex)
        {
            // Malformed image or Skia decoding failure — the original archive
            // may be corrupt. Log and return null so the caller 404s; other
            // pages of the same archive may still work.
            _logger.LogWarning(ex, "Failed to build thumbnail for {Path} page {Page}",
                archivePath, pageNumber);
            return null;
        }

        if (bytes.Length > 0)
        {
            _cache.Set(key, bytes, CacheOptions);
        }
        return bytes.Length > 0 ? bytes : null;
    }

    private static byte[] ResizeToJpeg(byte[] source, int targetWidth)
    {
        using var src = SKBitmap.Decode(source);
        if (src is null) return Array.Empty<byte>();

        // No point upscaling — page images are invariably larger than our
        // thumbnail targets (comic archives ship full-resolution pages). In
        // the rare case the source is already smaller, skip the resize to
        // keep the original fidelity.
        if (src.Width <= targetWidth)
        {
            using var img0 = SKImage.FromBitmap(src);
            using var data0 = img0.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data0.ToArray();
        }

        var ratio = (float)targetWidth / src.Width;
        var targetHeight = Math.Max(1, (int)(src.Height * ratio));
        using var resized = src.Resize(
            new SKImageInfo(targetWidth, targetHeight),
            SKSamplingOptions.Default);
        if (resized is null) return Array.Empty<byte>();

        using var img = SKImage.FromBitmap(resized);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }
}
