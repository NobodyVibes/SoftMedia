using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Collections.Concurrent;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Authorize(Policy = ScopePolicies.ReadLibrary)] // B-18: proxied artwork = catalog data
[EnableRateLimiting(ServiceCollectionExtensions.ImageProxyRateLimitPolicy)]
[Route("api/v1/[controller]")]
public class ImageController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageController> _logger;
    private readonly IThumbnailService _thumbnailService;
    private readonly IProxyImageStore _proxyStore;

    // Maximum file size: 10MB (same as ImageCacheService)
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    // Thumbnail width bounds (same as MusicController)
    private const int MinThumbnailWidth = 64;
    private const int MaxThumbnailWidth = 800;

    // Limit concurrent outbound fetches to avoid overwhelming upstream hosts
    private static readonly SemaphoreSlim _fetchSemaphore = new(8, 8);

    // Host allowlist, scheme guard and redirect policy live in ImageFetchPolicy
    // (MC-WI-002). This controller previously carried its own copy with a BROAD
    // ".archive.org" suffix, which admitted web.archive.org — the Wayback Machine, a
    // content-rewriting fetch proxy — while the downloader had already been tightened
    // to the anchored storage-node suffixes (audit wave-2 L-26). One shared policy now.

    public ImageController(IHttpClientFactory httpClientFactory, ILogger<ImageController> logger, IThumbnailService thumbnailService, IProxyImageStore proxyStore)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _thumbnailService = thumbnailService;
        _proxyStore = proxyStore;
    }

    /// <summary>
    /// Proxy and cache remote images with security validations. The cached copy is
    /// TRANSIENT: the image download queue deletes it once the permanent item-keyed copy
    /// lands, and IProxyImageStore's age sweep expires whatever is left (cache hits
    /// refresh the file's mtime so in-use entries survive).
    /// </summary>
    [HttpGet("proxy")]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client)] // Cache for 7 days in browser
    public async Task<IActionResult> ProxyImage([FromQuery] string url, [FromQuery] int? width)
    {
        if (string.IsNullOrEmpty(url))
        {
            return BadRequest("URL is required");
        }

        // Security: Validate URL and host (shared scheme guard + allowlist).
        if (!ImageFetchPolicy.TryValidateUrl(url, out var uri))
        {
            return BadRequest("Invalid URL format");
        }

        if (!ImageFetchPolicy.IsHostAllowed(uri.Host))
        {
            _logger.LogWarning("Blocked proxy request for non-allowed host: {Host}", uri.Host);
            return BadRequest("Image source not allowed");
        }

        // Hash-keyed paths owned by the proxy store (which also handles deletion/expiry).
        var cachedFilePath = _proxyStore.GetCachedFilePath(url);
        var sentinelPath = _proxyStore.GetSentinelPath(cachedFilePath);
        var thumbnailKey = _proxyStore.GetThumbnailKey(url);

        // Check proxy cache first
        if (System.IO.File.Exists(cachedFilePath))
        {
            _proxyStore.TouchOnHit(cachedFilePath);
            return await ServeCachedImageAsync(cachedFilePath, thumbnailKey, width);
        }

        // Check negative cache — upstream previously returned non-success for this URL
        if (System.IO.File.Exists(sentinelPath))
        {
            return NotFound("Image not found at source.");
        }

        await _fetchSemaphore.WaitAsync();
        try
        {
            // Re-check caches after acquiring semaphore (another request may have populated it)
            if (System.IO.File.Exists(cachedFilePath))
            {
                _proxyStore.TouchOnHit(cachedFilePath);
                return await ServeCachedImageAsync(cachedFilePath, thumbnailKey, width);
            }
            if (System.IO.File.Exists(sentinelPath))
                return NotFound("Image not found at source.");

            // Named "ImageProxy" client carries SoftMediaUserAgentHandler — see
            // ServiceCollectionExtensions.AddMediaServices. Do NOT spoof a
            // browser UA: SDD §4.3 requires honest attribution to upstream
            // metadata hosts (Wikidata, MusicBrainz, Open Library).
            var client = _httpClientFactory.CreateClient("ImageProxy");

            // Follows redirects manually, re-validating the allowlist on each hop (the
            // client has AllowAutoRedirect=false). null = the chain left the allowlist.
            using var response = await ImageFetchPolicy.GetWithAllowlistedRedirectsAsync(client, url, _logger);
            if (response == null)
            {
                // A blocked redirect is a policy decision (host allow-list / scheme),
                // NOT a definitive upstream 404 — do not persist a no-TTL negative-cache
                // sentinel here, otherwise a later allow-list widening (e.g. the Cover
                // Art Archive datanode fix) can't self-heal and the image stays blank.
                // A genuine upstream non-success below still gets a sentinel.
                _logger.LogWarning("Blocked image proxy redirect chain starting at {Url}", url);
                return NotFound("Image not found at source.");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Upstream returned {Status} for {Url}", response.StatusCode, url);
                // Write negative cache sentinel so retries skip the network request
                await System.IO.File.WriteAllTextAsync(sentinelPath, ((int)response.StatusCode).ToString());
                return NotFound("Image not found at source.");
            }

            // Security: Validate content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!ImageFetchPolicy.AllowedContentTypes.Contains(contentType))
            {
                _logger.LogWarning("Invalid content type {Type} from {Url}", contentType, url);
                return BadRequest("Invalid image type");
            }
            
            // Security: Check content length
            var contentLength = response.Content.Headers.ContentLength ?? 0;
            if (contentLength > MaxFileSizeBytes)
            {
                _logger.LogWarning("Image too large ({Size} bytes) from {Url}", contentLength, url);
                return BadRequest("Image too large");
            }

            // Stream download with size enforcement
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = System.IO.File.Create(cachedFilePath);
            
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > MaxFileSizeBytes)
                {
                    fileStream.Close();
                    System.IO.File.Delete(cachedFilePath);
                    return BadRequest("Image exceeded size limit");
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            }

            _logger.LogDebug("Cached proxy image: {Url} -> {Path}", url, cachedFilePath);
            return await ServeCachedImageAsync(cachedFilePath, thumbnailKey, width);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout fetching image: {Url}", url);
            return StatusCode(504, "Upstream timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying image: {Url}", url);
            return StatusCode(502, "Error fetching upstream image.");
        }
        finally
        {
            _fetchSemaphore.Release();
        }
    }

    /// <summary>
    /// Serve a cached image, optionally generating a resized WebP thumbnail
    /// (keyed by the store's URL-derived guid, so it never collides with item ids).
    /// </summary>
    private async Task<IActionResult> ServeCachedImageAsync(string cachedFilePath, Guid thumbnailKey, int? width)
    {
        var servePath = cachedFilePath;
        var serveMime = GetContentType(cachedFilePath);

        if (width.HasValue && width.Value >= MinThumbnailWidth && width.Value <= MaxThumbnailWidth)
        {
            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(
                cachedFilePath, thumbnailKey, width.Value);
            if (thumbPath != null)
            {
                servePath = thumbPath;
                serveMime = "image/webp";
            }
        }

        return PhysicalFile(servePath, serveMime, enableRangeProcessing: true);
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}

