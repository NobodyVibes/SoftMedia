using System.Collections.Concurrent;
using SkiaSharp;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// On-demand thumbnail generation service.
/// Generates WebP thumbnails and caches them on disk.
/// Uses per-key semaphores to prevent thundering herd on concurrent requests for the same image.
/// </summary>
public class ThumbnailService : IThumbnailService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ThumbnailService(IWebHostEnvironment env, ILogger<ThumbnailService> logger)
    {
        _env = env;
        _logger = logger;

        var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
            ? _env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        _cacheDirectory = Path.Combine(webRoot, "cache", "images", "thumbnails");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<string?> GetOrCreateThumbnailAsync(string sourcePath, Guid mediaItemId, int width)
    {
        var cacheFileName = $"{mediaItemId}_{width}.webp";
        var cachePath = Path.Combine(_cacheDirectory, cacheFileName);

        // Fast path: thumbnail already exists
        if (File.Exists(cachePath))
            return cachePath;

        // Acquire per-key lock to prevent duplicate generation
        var semaphore = _locks.GetOrAdd(cacheFileName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (File.Exists(cachePath))
                return cachePath;

            return await GenerateThumbnailAsync(sourcePath, cachePath, width);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private Task<string?> GenerateThumbnailAsync(string sourcePath, string cachePath, int targetWidth)
    {
        return Task.Run(() =>
        {
            try
            {
                // Security (audit wave-2 H-3): reject decode-bombs by checking the header
                // dimensions BEFORE SKBitmap.Decode allocates the full pixel buffer.
                if (!Helpers.ImageSafety.IsDecodableWithinBudget(sourcePath))
                {
                    _logger.LogWarning("Refusing to decode oversized/undecodable image: {Path}", sourcePath);
                    return null;
                }

                // EXIF orientation must be applied here: browsers auto-rotate originals
                // from their EXIF, but the WebP re-encode below strips it — without this,
                // portrait phone photos render sideways as thumbnails.
                var origin = SKEncodedOrigin.TopLeft;
                using (var codec = SKCodec.Create(sourcePath))
                {
                    if (codec != null) origin = codec.EncodedOrigin;
                }

                var original = SKBitmap.Decode(sourcePath);
                if (original == null)
                {
                    _logger.LogWarning("Failed to decode image: {Path}", sourcePath);
                    return null;
                }
                original = ApplyExifOrigin(original, origin);
                using var _ = original;

                // Skip if source is already smaller than target
                if (original.Width <= targetWidth)
                {
                    // Source is small enough — just encode as WebP without resizing
                    using var smallImage = SKImage.FromBitmap(original);
                    using var smallData = smallImage.Encode(SKEncodedImageFormat.Webp, 80);
                    using var smallStream = File.OpenWrite(cachePath);
                    smallData.SaveTo(smallStream);
                    return cachePath;
                }

                // Calculate proportional height
                var ratio = (float)targetWidth / original.Width;
                var targetHeight = (int)(original.Height * ratio);

                using var resized = original.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
                if (resized == null)
                {
                    _logger.LogWarning("Failed to resize image: {Path}", sourcePath);
                    return null;
                }

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Webp, 80);

                // Write to temp file then rename for atomicity
                var tempPath = cachePath + ".tmp";
                using (var stream = File.OpenWrite(tempPath))
                {
                    data.SaveTo(stream);
                }
                File.Move(tempPath, cachePath, overwrite: true);

                _logger.LogDebug("Generated thumbnail: {CachePath} ({Width}px)", cachePath, targetWidth);
                return cachePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating thumbnail for {Path}", sourcePath);
                return null;
            }
        });
    }

    /// <summary>
    /// Bakes the EXIF orientation into the pixel data. Maps each source pixel through the
    /// affine transform for its <see cref="SKEncodedOrigin"/> (orientations 5–8 swap axes).
    /// Returns the input unchanged for TopLeft; otherwise disposes the input and returns
    /// the reoriented copy.
    /// </summary>
    private static SKBitmap ApplyExifOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;

        float w = src.Width, h = src.Height;
        var swapAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // Row-major 2x3 affine: x' = m00*x + m01*y + m02 ; y' = m10*x + m11*y + m12
        SKMatrix matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirror X
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // rotate 180
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // mirror Y
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transpose
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // rotate 90 CW
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // rotate 270 CW
            _ => SKMatrix.Identity,
        };

        var dst = new SKBitmap(swapAxes ? src.Height : src.Width, swapAxes ? src.Width : src.Height);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(src, 0, 0);
        }
        src.Dispose();
        return dst;
    }
}
