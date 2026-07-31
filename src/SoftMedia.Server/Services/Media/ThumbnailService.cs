using System.Collections.Concurrent;
using System.Diagnostics;
using SkiaSharp;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// On-demand thumbnail generation service.
/// Generates WebP thumbnails and caches them on disk.
/// Uses per-key semaphores to prevent thundering herd on concurrent requests for the same image.
/// Formats SkiaSharp has no codec for (HEIC/HEIF from iPhones) fall back to the bundled ffmpeg.
/// </summary>
public class ThumbnailService : IThumbnailService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly IBinaryLocationService _binaryLocation;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ThumbnailService(IWebHostEnvironment env, ILogger<ThumbnailService> logger, IBinaryLocationService binaryLocation)
    {
        _env = env;
        _logger = logger;
        _binaryLocation = binaryLocation;

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

        // Fast path: thumbnail already exists. MC-WI-009: refresh mtime so the orphan
        // sweep's min-age guard (which reaps unknown keys — e.g. proxy-derived ones — by
        // age) treats in-use thumbnails as fresh instead of reaping and regenerating
        // them every cycle. Best-effort.
        if (File.Exists(cachePath))
        {
            try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); } catch { }
            return cachePath;
        }

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

    public int DeleteThumbnails(Guid key)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_cacheDirectory)) return 0;
            foreach (var file in Directory.GetFiles(_cacheDirectory, $"{key}_*.webp"))
            {
                File.Delete(file);
                deleted++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete thumbnails for key {Key}", key);
        }
        return deleted;
    }

    public int CleanupOrphans(HashSet<Guid> validKeys, TimeSpan minAge)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_cacheDirectory)) return 0;
            var cutoff = DateTime.UtcNow - minAge;

            foreach (var file in Directory.GetFiles(_cacheDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                    // "{guid}_{width}.webp"; a stale ".tmp"/".tmp.webp" from a crashed
                    // generation has no parseable guid and falls through to deletion too.
                    var name = Path.GetFileNameWithoutExtension(file);
                    var keyPart = name.Split('_')[0];
                    if (Guid.TryParse(keyPart, out var key) && validKeys.Contains(key)) continue;

                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process thumbnail during cleanup: {Path}", file);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Thumbnail cleanup removed {Count} orphaned file(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail orphan cleanup failed");
        }
        return deleted;
    }

    private async Task<string?> GenerateThumbnailAsync(string sourcePath, string cachePath, int targetWidth)
    {
        // Distinguish "no SkiaSharp codec" (HEIC → ffmpeg fallback) from "decodable but
        // over the pixel budget" (decode-bomb → refuse OUTRIGHT; routing bombs into
        // ffmpeg would just move the resource exhaustion, audit wave-2 H-3).
        using (var probe = SKCodec.Create(sourcePath))
        {
            if (probe == null)
            {
                return await TryGenerateWithFfmpegAsync(sourcePath, cachePath, targetWidth);
            }
            if (!Helpers.ImageSafety.IsWithinBudget(probe.Info.Width, probe.Info.Height))
            {
                _logger.LogWarning("Refusing to decode oversized image: {Path}", sourcePath);
                return null;
            }
        }

        return await Task.Run(() =>
        {
            try
            {

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
    /// ffmpeg fallback for formats SkiaSharp has no codec for — in practice HEIC/HEIF
    /// from iPhones. ffmpeg decodes, scales (never upscales), auto-applies the HEIF
    /// rotation properties, and writes the same WebP the Skia path produces, so
    /// callers can't tell which pipeline served them. Null on any failure — the
    /// caller falls back to serving the original bytes.
    /// </summary>
    private async Task<string?> TryGenerateWithFfmpegAsync(string sourcePath, string cachePath, int targetWidth)
    {
        try
        {
            // Same argument-injection guard the scanners apply (audit H2) — ffmpeg gets
            // this path as a process argument.
            if (Helpers.MediaPathSafety.HasArgumentInjectionRisk(sourcePath)) return null;

            var ffmpeg = _binaryLocation.ResolveFFmpegPath();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg)) return null;

            var tempPath = cachePath + ".tmp.webp";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-vf");
            // \, escapes the comma for the filter parser; min() never upscales.
            psi.ArgumentList.Add($"scale=min({targetWidth}\\,iw):-1");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libwebp");
            psi.ArgumentList.Add("-quality");
            psi.ArgumentList.Add("80");
            psi.ArgumentList.Add(tempPath);

            using var process = Process.Start(psi);
            if (process == null) return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                _logger.LogWarning("ffmpeg thumbnail timed out for {Path}", sourcePath);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                _logger.LogDebug("ffmpeg thumbnail failed (exit {Code}) for {Path}", process.ExitCode, sourcePath);
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return null;
            }

            File.Move(tempPath, cachePath, overwrite: true);
            _logger.LogDebug("Generated thumbnail via ffmpeg: {CachePath} ({Width}px)", cachePath, targetWidth);
            return cachePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg thumbnail fallback failed for {Path}", sourcePath);
            return null;
        }
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
