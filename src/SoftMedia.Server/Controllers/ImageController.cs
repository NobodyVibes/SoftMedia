using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting(ServiceCollectionExtensions.ImageProxyRateLimitPolicy)]
[Route("api/v1/[controller]")]
public class ImageController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ImageController> _logger;
    private readonly IThumbnailService _thumbnailService;
    private readonly string _proxyCacheDir;

    // Maximum file size: 10MB (same as ImageCacheService)
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    // Thumbnail width bounds (same as MusicController)
    private const int MinThumbnailWidth = 64;
    private const int MaxThumbnailWidth = 800;

    // Limit concurrent outbound fetches to avoid overwhelming upstream hosts
    private static readonly SemaphoreSlim _fetchSemaphore = new(8, 8);
    
    // Allowed hosts for SSRF prevention (same as ImageCacheService)
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "static.tvmaze.com",
        "coverartarchive.org",
        "archive.org",
        "upload.wikimedia.org",
        "commons.wikimedia.org",
        "m.media-amazon.com",
        "ia.media-imdb.com",
        "covers.openlibrary.org"
    };
    
    // Allowed content types
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp"
    };

    public ImageController(IHttpClientFactory httpClientFactory, IWebHostEnvironment env, ILogger<ImageController> logger, IThumbnailService thumbnailService)
    {
        _httpClientFactory = httpClientFactory;
        _env = env;
        _logger = logger;
        _thumbnailService = thumbnailService;
        
        // Use wwwroot/cache/images/proxy for hash-based proxy caching
        _proxyCacheDir = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), 
            "cache", "images", "proxy");
        Directory.CreateDirectory(_proxyCacheDir);
    }

    /// <summary>
    /// Proxy and cache remote images with security validations.
    /// Checks structured cache first to avoid duplicates.
    /// </summary>
    [HttpGet("proxy")]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client)] // Cache for 7 days in browser
    public async Task<IActionResult> ProxyImage([FromQuery] string url, [FromQuery] int? width)
    {
        if (string.IsNullOrEmpty(url))
        {
            return BadRequest("URL is required");
        }

        // Security: Validate URL and host
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return BadRequest("Invalid URL format");
        }
        
        if (!AllowedHosts.Contains(uri.Host))
        {
            _logger.LogWarning("Blocked proxy request for non-allowed host: {Host}", uri.Host);
            return BadRequest("Image source not allowed");
        }

        // Create a safe filename from the URL hash
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var extension = GetExtensionFromUrl(url);
        var cachedFilePath = Path.Combine(_proxyCacheDir, hash + extension);

        var sentinelPath = cachedFilePath + ".404";

        // Check proxy cache first
        if (System.IO.File.Exists(cachedFilePath))
        {
            return await ServeCachedImageAsync(cachedFilePath, hash, width);
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
                return await ServeCachedImageAsync(cachedFilePath, hash, width);
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
            using var response = await GetWithAllowlistedRedirectsAsync(client, url);
            if (response == null)
            {
                _logger.LogWarning("Blocked image proxy redirect chain starting at {Url}", url);
                await System.IO.File.WriteAllTextAsync(sentinelPath, "blocked-redirect");
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
            if (!AllowedContentTypes.Contains(contentType))
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
            return await ServeCachedImageAsync(cachedFilePath, hash, width);
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

    // Maximum redirect hops to follow before giving up.
    private const int MaxRedirects = 5;

    /// <summary>
    /// Issues a GET and follows up to <see cref="MaxRedirects"/> redirects MANUALLY,
    /// re-validating each hop's host against <see cref="AllowedHosts"/> (the client has
    /// AllowAutoRedirect=false). This stops an allowlisted host from redirecting the
    /// proxy to an internal address (cloud metadata, loopback, RFC1918) — a host check
    /// only on the first URL would otherwise be bypassed by the 3xx. Returns null when
    /// the chain leaves the allowlist or exceeds the hop limit.
    /// </summary>
    private async Task<HttpResponseMessage?> GetWithAllowlistedRedirectsAsync(HttpClient client, string url)
    {
        var currentUrl = url;
        for (var hop = 0; ; hop++)
        {
            var response = await client.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead);

            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400)
                return response; // not a redirect — success or error, caller decides

            var location = response.Headers.Location;
            response.Dispose();

            if (hop >= MaxRedirects)
            {
                _logger.LogWarning("Image proxy redirect chain exceeded {Max} hops for {Url}", MaxRedirects, url);
                return null;
            }
            if (location == null)
            {
                _logger.LogWarning("Image proxy redirect with no Location header from {Url}", currentUrl);
                return null;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(new Uri(currentUrl), location);
            if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                || !AllowedHosts.Contains(next.Host))
            {
                _logger.LogWarning("Blocked image proxy redirect to non-allowlisted target {Target} (from {Url})", next, currentUrl);
                return null;
            }

            currentUrl = next.AbsoluteUri;
        }
    }

    /// <summary>
    /// Serve a cached image, optionally generating a resized WebP thumbnail.
    /// </summary>
    private async Task<IActionResult> ServeCachedImageAsync(string cachedFilePath, string urlHash, int? width)
    {
        var servePath = cachedFilePath;
        var serveMime = GetContentType(cachedFilePath);

        if (width.HasValue && width.Value >= MinThumbnailWidth && width.Value <= MaxThumbnailWidth)
        {
            // Derive a deterministic GUID from the URL hash for ThumbnailService's file naming
            var guidBytes = new byte[16];
            Array.Copy(SHA256.HashData(Encoding.UTF8.GetBytes(urlHash)), guidBytes, 16);
            var proxyGuid = new Guid(guidBytes);

            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(
                cachedFilePath, proxyGuid, width.Value);
            if (thumbPath != null)
            {
                servePath = thumbPath;
                serveMime = "image/webp";
            }
        }

        return PhysicalFile(servePath, serveMime, enableRangeProcessing: true);
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 5 && 
                (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp"))
            {
                return ext;
            }
        }
        catch { }
        return ".jpg"; // Default
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

