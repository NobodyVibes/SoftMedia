using System.Net.Http.Headers;

namespace SoftMedia.Server.Services;

/// <summary>
/// Service for caching remote images locally with security validations.
/// Prevents SSRF attacks, path traversal, and validates content types.
/// </summary>
public class ImageCacheService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly string _basePath;
    
    // Maximum file size: 10MB
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    
    // Allowed image content types
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp"
    };
    
    // Allowed URL hosts (allowlist for SSRF prevention)
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // TVMaze
        "static.tvmaze.com",
        // MusicBrainz / Cover Art Archive
        "coverartarchive.org",
        "archive.org",
        // Wikidata / Wikimedia
        "upload.wikimedia.org",
        "commons.wikimedia.org",
        // OMDb (posters hosted on Amazon)
        "m.media-amazon.com",
        "ia.media-imdb.com"
    };

    public ImageCacheService(HttpClient httpClient, ILogger<ImageCacheService> logger, IWebHostEnvironment env)
    {
        _httpClient = httpClient;
        _logger = logger;
        _env = env;
        _basePath = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "cache", "images");
        
        // Ensure base directories exist
        Directory.CreateDirectory(Path.Combine(_basePath, "tv"));
        Directory.CreateDirectory(Path.Combine(_basePath, "movies"));
        Directory.CreateDirectory(Path.Combine(_basePath, "music"));
    }

    /// <summary>
    /// Cache a series poster from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheSeriesPosterAsync(Guid seriesId, string remoteUrl)
    {
        return await CacheImageAsync($"tv/{seriesId}_poster", remoteUrl);
    }
    
    /// <summary>
    /// Cache an episode still from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheEpisodeStillAsync(Guid seriesId, int season, int episode, string remoteUrl)
    {
        return await CacheImageAsync($"tv/{seriesId}_s{season:D2}e{episode:D2}_still", remoteUrl);
    }
    
    /// <summary>
    /// Cache a season poster from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheSeasonPosterAsync(Guid seriesId, int season, string remoteUrl)
    {
        return await CacheImageAsync($"tv/{seriesId}_season{season:D2}_poster", remoteUrl);
    }
    
    /// <summary>
    /// Cache a movie poster from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheMoviePosterAsync(Guid movieId, string remoteUrl)
    {
        return await CacheImageAsync($"movies/{movieId}_poster", remoteUrl);
    }
    
    /// <summary>
    /// Cache an album cover from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheAlbumCoverAsync(Guid albumId, string remoteUrl)
    {
        return await CacheImageAsync($"music/{albumId}_cover", remoteUrl);
    }
    
    /// <summary>
    /// Delete cached image for a media item (used when item is deleted).
    /// </summary>
    public void DeleteImageForMediaItem(Guid mediaItemId, Models.MediaType type)
    {
        var prefix = type switch
        {
            Models.MediaType.Series => $"tv/{mediaItemId}",
            Models.MediaType.Episode => $"tv/{mediaItemId}",
            Models.MediaType.Movie => $"movies/{mediaItemId}",
            Models.MediaType.Audio => $"music/{mediaItemId}",
            Models.MediaType.Album => $"music/{mediaItemId}",
            Models.MediaType.Artist => $"music/{mediaItemId}",
            _ => null
        };
        
        if (prefix == null) return;
        
        try
        {
            var directory = Path.GetDirectoryName(Path.Combine(_basePath, prefix));
            if (directory != null && Directory.Exists(directory))
            {
                var files = Directory.GetFiles(directory, $"{Path.GetFileName(prefix)}*");
                foreach (var file in files)
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted cached image: {Path}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached images for {Id}", mediaItemId);
        }
    }
    
    /// <summary>
    /// Delete all cached images for media items in a library.
    /// </summary>
    public void DeleteImagesForLibrary(IEnumerable<(Guid Id, Models.MediaType Type)> mediaItems)
    {
        foreach (var (id, type) in mediaItems)
        {
            DeleteImageForMediaItem(id, type);
        }
    }
    
    /// <summary>
    /// Clean up orphaned cached images (files that don't correspond to any media item).
    /// Returns count of deleted files.
    /// </summary>
    public int CleanupOrphanedImages(HashSet<Guid> validMediaIds)
    {
        int deletedCount = 0;
        
        try
        {
            // Check each subdirectory
            foreach (var subDir in new[] { "tv", "movies", "music" })
            {
                var dirPath = Path.Combine(_basePath, subDir);
                if (!Directory.Exists(dirPath)) continue;
                
                foreach (var file in Directory.GetFiles(dirPath, "*.*"))
                {
                    try
                    {
                        // Extract GUID from filename (format: {guid}_poster.jpg or {guid}_s01e01_still.jpg)
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var guidPart = fileName.Split('_')[0];
                        
                        if (Guid.TryParse(guidPart, out var mediaId))
                        {
                            if (!validMediaIds.Contains(mediaId))
                            {
                                File.Delete(file);
                                deletedCount++;
                                _logger.LogDebug("Deleted orphaned image: {Path}", file);
                            }
                        }
                        else
                        {
                            // Invalid filename format, delete it
                            File.Delete(file);
                            deletedCount++;
                            _logger.LogDebug("Deleted invalid image file: {Path}", file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process file during cleanup: {Path}", file);
                    }
                }
            }
            
            _logger.LogInformation("Cleaned up {Count} orphaned cached images", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during orphaned image cleanup");
        }
        
        return deletedCount;
    }

    private async Task<string> CacheImageAsync(string relativePath, string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        try
        {
            // Security: Validate URL host against allowlist (SSRF prevention)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Invalid URL format: {Url}", url);
                return url;
            }
            
            _logger.LogInformation("Attempting to cache image from host: {Host}, URL: {Url}", uri.Host, url);
            
            if (!AllowedHosts.Contains(uri.Host))
            {
                _logger.LogWarning("URL host not in allowlist: {Host}. Allowed hosts: {AllowedHosts}", 
                    uri.Host, string.Join(", ", AllowedHosts));
                return url;
            }
            
            // Security: Sanitize path to prevent directory traversal
            var sanitizedPath = SanitizePath(relativePath);
            var fullPath = Path.GetFullPath(Path.Combine(_basePath, sanitizedPath));
            
            // Security: Ensure path is within cache directory (normalize separators for cross-platform)
            var normalizedBasePath = Path.GetFullPath(_basePath).Replace('\\', '/');
            var normalizedFullPath = fullPath.Replace('\\', '/');
            if (!normalizedFullPath.StartsWith(normalizedBasePath))
            {
                _logger.LogWarning("Path traversal attempt blocked: {Path}", relativePath);
                return url;
            }
            
            // Check if already cached (with any extension)
            var existingFiles = Directory.GetFiles(Path.GetDirectoryName(fullPath)!, $"{Path.GetFileName(fullPath)}.*");
            if (existingFiles.Length > 0)
            {
                // Return existing cached file URL
                var existingFile = existingFiles[0];
                return $"/cache/images/{relativePath}{Path.GetExtension(existingFile)}";
            }
            
            // Download with timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download image: {Status} from {Url}", response.StatusCode, url);
                return url;
            }
            
            // Security: Validate content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!AllowedContentTypes.Contains(contentType))
            {
                _logger.LogWarning("Invalid content type: {ContentType} from {Url}", contentType, url);
                return url;
            }
            
            // Security: Check content length
            var contentLength = response.Content.Headers.ContentLength ?? 0;
            if (contentLength > MaxFileSizeBytes)
            {
                _logger.LogWarning("Image too large: {Size} bytes from {Url}", contentLength, url);
                return url;
            }
            
            // Determine file extension from content type
            var extension = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".jpg"
            };
            
            var finalPath = fullPath + extension;
            
            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            
            // Download and save with streaming to avoid memory issues
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = File.Create(finalPath);
            
            // Read in chunks, enforcing size limit
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > MaxFileSizeBytes)
                {
                    // Exceeded size limit mid-download, clean up
                    fileStream.Close();
                    File.Delete(finalPath);
                    _logger.LogWarning("Image exceeded size limit during download: {Url}", url);
                    return url;
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
            }
            
            _logger.LogDebug("Cached image: {Url} -> {Path}", url, finalPath);
            return $"/cache/images/{sanitizedPath}{extension}";
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Image download timed out: {Url}", url);
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache image: {Url}", url);
            return url;
        }
    }
    
    private static string SanitizePath(string path)
    {
        // Remove any directory traversal attempts and normalize separators
        return path
            .Replace("..", "")
            .Replace("\\", "/")
            .Trim('/', ' ');
    }
}
