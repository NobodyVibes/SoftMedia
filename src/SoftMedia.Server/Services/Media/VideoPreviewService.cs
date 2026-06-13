using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media;

public interface IVideoPreviewService
{
    Task<(byte[]? Data, string ContentType)> GetPreviewImageAsync(Guid mediaId, double time);
}

public class VideoPreviewService : IVideoPreviewService
{
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VideoPreviewService> _logger;
    
    // Cache for frame previews
    private static readonly Dictionary<string, (byte[] Data, DateTime Expires)> _frameCache = new();
    private static readonly object _frameCacheLock = new();

    // Audit wave-2 L-21: bound concurrent frame extractions. Each is a separate CPU-heavy ffmpeg
    // decode that is NOT counted against the transcode session cap, so without this a flood of
    // distinct ?time= values could spawn unlimited ffmpeg processes and exhaust the host. Process-
    // wide gate; requests that can't get a slot promptly are shed rather than queued unboundedly.
    private const int MaxConcurrentExtractions = 4;
    private static readonly SemaphoreSlim _extractionGate = new(MaxConcurrentExtractions, MaxConcurrentExtractions);

    public VideoPreviewService(
        IBinaryLocationService binaryLocationService,
        IServiceScopeFactory scopeFactory,
        ILogger<VideoPreviewService> logger)
    {
        _binaryLocationService = binaryLocationService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<(byte[]? Data, string ContentType)> GetPreviewImageAsync(Guid mediaId, double time)
    {
        // Round time to 1 second to increase cache hits
        var roundedTime = Math.Floor(time);
        var cacheKey = $"{mediaId}_{roundedTime}";

        // Security (audit wave-2, frame-cache ACL bypass): resolve the item through the
        // ACL/rating-aware repository BEFORE consulting the shared static frame cache. The cache is
        // keyed only by (mediaId, time) and shared across all users, so reading it first let a
        // restricted user pull a frame that an allowed user had cached — bypassing the library ACL.
        // GetByIdAsync applies the per-user library ACL + content-rating ceiling and returns null
        // when the caller may not see the item; map that to "no frame".
        string mediaPath;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMediaRepository>();
            var mediaItem = await repository.GetByIdAsync(mediaId);
            if (mediaItem == null) return (null, string.Empty);
            mediaPath = mediaItem.Path;
        }

        // Now safe to serve from the shared cache — the caller has passed the access check above.
        lock (_frameCacheLock)
        {
            if (_frameCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                return (cached.Data, "image/jpeg");
            }
        }

        // Audit wave-2 L-21: acquire a frame-extraction slot or shed the request, so a flood of
        // distinct timestamps can't spawn unbounded concurrent ffmpeg decodes.
        if (!await _extractionGate.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("Frame-extraction gate saturated; shedding request for {MediaId}", mediaId);
            return (null, string.Empty);
        }

        // Extract single frame using FFmpeg
        var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
        var tempFile = Path.Combine(Path.GetTempPath(), $"frame_{mediaId}_{roundedTime}.jpg");

        try
        {
            // Use FFmpeg to extract frame at timestamp (fast seek with -ss before -i)
            var arguments = $"-ss {roundedTime:F0} -i \"{mediaPath}\" -vframes 1 -q:v 8 -vf scale=320:-1 -f image2 -y \"{tempFile}\"";
            _logger.LogDebug("FFmpeg frame extraction: {Args}", arguments);
            
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start FFmpeg process");
                return (null, string.Empty);
            }
            
            // Read stderr asynchronously
            var stderrTask = process.StandardError.ReadToEndAsync();
            
            // Wait with timeout of 5 seconds (increased for slower files)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout - kill the process
                try { process.Kill(true); } catch { }
                _logger.LogWarning("Frame extraction timed out for {MediaId} at {Time}s", mediaId, time);
                return (null, string.Empty);
            }
            
            var stderr = await stderrTask;
            
            if (process.ExitCode != 0)
            {
                _logger.LogError("FFmpeg frame extraction failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr);
                return (null, string.Empty);
            }

            if (!File.Exists(tempFile))
            {
                _logger.LogError("FFmpeg did not create output file. Stderr: {Stderr}", stderr);
                return (null, string.Empty);
            }

            var bytes = await File.ReadAllBytesAsync(tempFile);
            
            if (bytes.Length == 0)
            {
                _logger.LogError("FFmpeg created empty file. Stderr: {Stderr}", stderr);
                return (null, string.Empty);
            }
            
            // Cache for 30 seconds
            lock (_frameCacheLock)
            {
                _frameCache[cacheKey] = (bytes, DateTime.UtcNow.AddSeconds(30));
                
                // Cleanup old cache entries
                var expiredKeys = _frameCache.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList();
                foreach (var key in expiredKeys) _frameCache.Remove(key);
            }
            
            // Cleanup temp file
            try { File.Delete(tempFile); } catch { }
            
            return (bytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting frame for {MediaId} at {Time}", mediaId, time);
            return (null, string.Empty);
        }
        finally
        {
            _extractionGate.Release();
        }
    }
}
