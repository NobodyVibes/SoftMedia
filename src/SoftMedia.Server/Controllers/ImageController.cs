using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ImageController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheDirectory;
    private readonly ILogger<ImageController> _logger;

    public ImageController(IHttpClientFactory httpClientFactory, IWebHostEnvironment env, ILogger<ImageController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheDirectory = Path.Combine(env.ContentRootPath, "cache", "images");
        _logger = logger;

        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    [HttpGet("proxy")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)] // Cache for 1 day in browser
    public async Task<IActionResult> ProxyImage([FromQuery] string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return BadRequest("URL is required");
        }

        // Create a safe filename from the URL
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var extension = Path.GetExtension(url).Split('?')[0];
        if (string.IsNullOrEmpty(extension) || extension.Length > 5) extension = ".jpg"; // Default fallback
        
        var cachedFilePath = Path.Combine(_cacheDirectory, hash + extension);

        // Check if cached
        if (System.IO.File.Exists(cachedFilePath))
        {
            var contentType = GetContentType(cachedFilePath);
            return PhysicalFile(cachedFilePath, contentType);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            // Mimic a browser to avoid some anti-bot protections
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound("Image not found at source.");
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            await System.IO.File.WriteAllBytesAsync(cachedFilePath, imageBytes);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? GetContentType(cachedFilePath);
            return PhysicalFile(cachedFilePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying image: {Url}", url);
            return StatusCode(502, "Error fetching upstream image.");
        }
    }

    private string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}
