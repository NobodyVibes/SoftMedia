using System.Net.Http.Headers;

using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for caching remote images locally with security validations.
/// Prevents SSRF attacks, path traversal, and validates content types.
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly string _basePath;
    
    // Maximum file size: 10MB
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    
    // Host allowlist, scheme guard and redirect policy live in ImageFetchPolicy
    // (MC-WI-002) — shared with the on-demand proxy so the two can never drift again.

    private readonly Abstractions.IStreamSecurityService _streamSecurity;

    public ImageCacheService(HttpClient httpClient, ILogger<ImageCacheService> logger, IWebHostEnvironment env,
        Abstractions.IStreamSecurityService streamSecurity)
    {
        _httpClient = httpClient;
        _logger = logger;
        _env = env;
        _streamSecurity = streamSecurity;
        _basePath = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "cache", "images");
        
        // Ensure base directories exist
        Directory.CreateDirectory(Path.Combine(_basePath, "tv"));
        Directory.CreateDirectory(Path.Combine(_basePath, "tv", "cast"));
        Directory.CreateDirectory(Path.Combine(_basePath, "movies"));
        Directory.CreateDirectory(Path.Combine(_basePath, "music"));
        Directory.CreateDirectory(Path.Combine(_basePath, "games"));
        Directory.CreateDirectory(Path.Combine(_basePath, "books"));
    }

    /// <summary>
    /// Cache a series poster from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheSeriesPosterAsync(Guid seriesId, string remoteUrl)
    {
        return await CacheImageAsync($"tv/{seriesId}_poster", remoteUrl);
    }

    /// <summary>
    /// Cache a cast member image. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheCastImageAsync(int personId, string remoteUrl)
    {
        return await CacheImageAsync($"tv/cast/{personId}", remoteUrl);
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
    /// Cache a game poster from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheGamePosterAsync(Guid gameId, string remoteUrl)
    {
        return await CacheImageAsync($"games/{gameId}_poster", remoteUrl);
    }
    
    /// <summary>
    /// Cache a book cover from remote URL. Returns local URL path or original URL on failure.
    /// </summary>
    public async Task<string> CacheBookPosterAsync(Guid bookId, string remoteUrl)
    {
        return await CacheImageAsync($"books/{bookId}_poster", remoteUrl);
    }
    
    // R-WI-014 — file extensions a local sidecar may carry (mirrors AllowedContentTypes).
    private static readonly HashSet<string> AllowedLocalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    /// <inheritdoc />
    public async Task<string?> CacheLocalImageAsync(string sourceFilePath, string cacheKey, string jailRoot)
    {
        try
        {
            // Review HIGH: a symlinked poster.jpg (or an NFO thumb pointing at a link) must not
            // exfiltrate arbitrary readable files into the publicly served /cache. IsPathAuthorized
            // canonicalises with per-component symlink resolution (audit M-7) before the prefix
            // check — the same jail the streaming path uses.
            if (string.IsNullOrEmpty(jailRoot) ||
                !_streamSecurity.IsPathAuthorized(sourceFilePath, new[] { jailRoot }))
            {
                _logger.LogWarning("Local artwork {Path} failed the jail check against {Root}; skipping.", sourceFilePath, jailRoot);
                return null;
            }

            var source = new FileInfo(sourceFilePath);
            if (!source.Exists) return null;
            var extension = source.Extension;
            if (!AllowedLocalExtensions.Contains(extension))
            {
                _logger.LogWarning("Local artwork {Path} has disallowed extension; skipping.", sourceFilePath);
                return null;
            }
            if (source.Length == 0 || source.Length > MaxFileSizeBytes)
            {
                _logger.LogWarning("Local artwork {Path} is empty or exceeds {Max} bytes; skipping.", sourceFilePath, MaxFileSizeBytes);
                return null;
            }

            // Same traversal jail as the download path: sanitize + prefix-check under _basePath.
            var sanitizedPath = SanitizePath(cacheKey);
            var fullPath = Path.GetFullPath(Path.Combine(_basePath, sanitizedPath));
            var normalizedBasePath = Path.GetFullPath(_basePath).Replace('\\', '/');
            if (!fullPath.Replace('\\', '/').StartsWith(normalizedBasePath))
            {
                _logger.LogWarning("Path traversal attempt blocked for local artwork key: {Key}", cacheKey);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // EXACT freshness: the copy carries the SOURCE's mtime, so "cached (mtime,size)
            // equals source (mtime,size)" means "this exact file is already ingested" — a
            // swapped sidecar (even with an older preserved mtime) re-copies. Local keys are
            // distinct from provider keys, so no foreign file can ever satisfy this check.
            var existing = Directory.GetFiles(Path.GetDirectoryName(fullPath)!, $"{Path.GetFileName(fullPath)}.*");
            var target = fullPath + extension;
            // ±2s tolerance: filesystems with coarse timestamp resolution (FAT/exFAT 2s, some
            // SMB mounts) can't store the source's exact mtime — strict equality would re-copy
            // on every scan forever. 2s is far below any human-swaps-the-poster interval.
            var upToDate = existing.FirstOrDefault(f =>
                string.Equals(f, target, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((File.GetLastWriteTimeUtc(f) - source.LastWriteTimeUtc).TotalSeconds) <= 2
                && new FileInfo(f).Length == source.Length);
            if (upToDate != null)
            {
                return $"/cache/images/{sanitizedPath}{Path.GetExtension(upToDate)}";
            }
            foreach (var stale in existing)
            {
                try { File.Delete(stale); } catch { /* best effort; copy below overwrites the same name */ }
            }

            // Async copy so a large file on slow storage doesn't block the scan thread pool.
            await using (var src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst);
            }
            File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc); // the freshness stamp

            _logger.LogInformation("Cached local artwork {Source} as {Key}{Ext}", sourceFilePath, sanitizedPath, extension);
            return $"/cache/images/{sanitizedPath}{extension}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache local artwork {Path}", sourceFilePath);
            return null;
        }
    }

    /// <inheritdoc />
    public void DeleteCachedLocalImage(string cacheKey)
    {
        try
        {
            var sanitizedPath = SanitizePath(cacheKey);
            var fullPath = Path.GetFullPath(Path.Combine(_basePath, sanitizedPath));
            if (!fullPath.Replace('\\', '/').StartsWith(Path.GetFullPath(_basePath).Replace('\\', '/'))) return;
            var dir = Path.GetDirectoryName(fullPath);
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, $"{Path.GetFileName(fullPath)}.*"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached local artwork {Key}", cacheKey);
        }
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
            Models.MediaType.Game => $"games/{mediaItemId}",
            Models.MediaType.Book => $"books/{mediaItemId}",
            Models.MediaType.ComicSeries => $"books/{mediaItemId}",
            Models.MediaType.ComicIssue => $"books/{mediaItemId}",
            // Season posters and episode stills are keyed by the SERIES id
            // ("tv/{seriesId}_season01_poster"), so the Series case above covers them;
            // Photo has no provider artwork (thumbnails are cleaned separately).
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
    /// Delete cached cast images by the provider's EXTERNAL person ids — files are named
    /// "tv/cast/{Person.ExternalId}.{ext}", NOT by the Person primary key. (The library
    /// deletion path historically passed PKs here, which matched nothing — or worse, an
    /// unrelated person's file — so cast images always leaked.)
    /// </summary>
    public void DeleteCastImagesForExternalIds(IEnumerable<int> externalPersonIds)
    {
        var castDir = Path.Combine(_basePath, "tv", "cast");
        if (!Directory.Exists(castDir)) return;

        foreach (var externalId in externalPersonIds)
        {
            try
            {
                var files = Directory.GetFiles(castDir, $"{externalId}.*");
                foreach (var file in files)
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted cast image: {Path}", file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cast image for external person id {ExternalId}", externalId);
            }
        }
    }

    /// <summary>
    /// Delete cast images whose external id matches no entry in
    /// <paramref name="validExternalIds"/> — the ids of persons still referenced by at
    /// least one MediaItemCast row (row-existence: IsMissing items' cast rows count).
    /// Backstop for scan-driven hard deletes and for images leaked before the external-id
    /// fix above. Returns files deleted.
    /// </summary>
    public int CleanupOrphanedCastImages(HashSet<int> validExternalIds)
    {
        var deleted = 0;
        var castDir = Path.Combine(_basePath, "tv", "cast");
        if (!Directory.Exists(castDir)) return 0;

        try
        {
            foreach (var file in Directory.GetFiles(castDir))
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(stem, out var externalId) && validExternalIds.Contains(externalId))
                    {
                        continue;
                    }
                    File.Delete(file);
                    deleted++;
                    _logger.LogDebug("Deleted orphaned cast image: {Path}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process cast image during cleanup: {Path}", file);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Cast image cleanup removed {Count} orphaned file(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cast image orphan cleanup failed");
        }
        return deleted;
    }
    
    /// <summary>
    /// Every cache subdirectory whose files are keyed by a MediaItem guid ("{id}_kind.ext").
    /// tv/cast is deliberately absent: it is keyed by int external person ids and cleaned
    /// separately via <see cref="DeleteCastImagesForExternalIds"/> /
    /// <see cref="CleanupOrphanedCastImages"/> (and the top-level scans below are
    /// non-recursive, so it is never touched by accident).
    /// </summary>
    private static readonly string[] MediaCacheSubdirectories = { "tv", "movies", "music", "games", "books" };

    /// <summary>
    /// Clean up orphaned cached images (files whose guid corresponds to NO MediaItems row).
    /// SR-WI-037: the contract for <paramref name="validMediaIds"/> is ROW-EXISTENCE, not
    /// visibility — callers must pass the ids of ALL MediaItems rows INCLUDING soft-deleted
    /// (IsMissing) ones, whose artwork is RETAINED so it heals when the drive returns.
    /// Returns count of deleted files.
    /// </summary>
    public int CleanupOrphanedImages(HashSet<Guid> validMediaIds)
    {
        int deletedCount = 0;

        try
        {
            // Check each subdirectory (SR-WI-037 added "books" — covers were never cleaned)
            foreach (var subDir in MediaCacheSubdirectories)
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

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, string> GetCachedPosterPaths()
    {
        var found = new Dictionary<Guid, string>();

        foreach (var subDir in MediaCacheSubdirectories)
        {
            var dirPath = Path.Combine(_basePath, subDir);
            if (!Directory.Exists(dirPath)) continue;

            try
            {
                // "*_poster.*" already excludes "{id}_poster_local.jpg" / "_poster_nfo.jpg"
                // (the literal "_poster." must precede the extension); "{id}_season01_poster"
                // survives the glob but fails the guid parse below.
                foreach (var file in Directory.GetFiles(dirPath, "*_poster.*"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var guidPart = name[..^"_poster".Length];
                    if (Guid.TryParse(guidPart, out var mediaId))
                    {
                        found[mediaId] = $"/cache/images/{subDir}/{Path.GetFileName(file)}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate cached posters in {Dir}", dirPath);
            }
        }

        return found;
    }

    /// <inheritdoc />
    public Task<int> InvalidateCachedImagesAsync(Guid mediaItemId) => Task.Run(() =>
    {
        var deleted = 0;
        foreach (var subDir in MediaCacheSubdirectories)
        {
            var dirPath = Path.Combine(_basePath, subDir);
            if (!Directory.Exists(dirPath)) continue;

            // Keys are "{id}_<kind>"; a series id also prefixes its season posters and
            // episode stills, so invalidating a series drops those too (desired for a
            // series-level refresh).
            foreach (var file in Directory.GetFiles(dirPath, $"{mediaItemId}_*"))
            {
                // R-WI-014 local-sidecar copies ("{id}_poster_local.jpg") are NOT
                // provider-refreshable — deleting them would leave the DB's /cache URL
                // dangling until the next scan re-ingests the sidecar. Retain them.
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.EndsWith("_local", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    File.Delete(file);
                    deleted++;
                    _logger.LogDebug("Invalidated cached image: {Path}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to invalidate cached image {Path}", file);
                }
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Invalidated {Count} cached image(s) for media item {Id}", deleted, mediaItemId);
        }
        return deleted;
    });

    private async Task<string> CacheImageAsync(string relativePath, string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        try
        {
            // Security: Validate URL host against allowlist (SSRF prevention).
            if (!ImageFetchPolicy.TryValidateUrl(url, out var uri))
            {
                _logger.LogWarning("Invalid or non-http(s) URL: {Url}", url);
                return url;
            }

            _logger.LogInformation("Attempting to cache image from host: {Host}, URL: {Url}", uri.Host, url);

            if (!ImageFetchPolicy.IsHostAllowed(uri.Host))
            {
                _logger.LogWarning("URL host not in allowlist: {Host}. Allowed hosts: {AllowedHosts}",
                    uri.Host, string.Join(", ", ImageFetchPolicy.AllowedHosts));
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
            
            // Download with retry for transient failures (429 rate limiting, 5xx server errors)
            HttpResponseMessage? response = null;
            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    // Follows redirects manually, re-validating the host allowlist on each
                    // hop (the HttpClient has AllowAutoRedirect=false). null = the chain
                    // left the allowlist (SSRF guard) — treat as a failed download.
                    response = await ImageFetchPolicy.GetWithAllowlistedRedirectsAsync(_httpClient, url, _logger, cts.Token);
                }
                catch
                {
                    cts.Dispose();
                    throw;
                }
                cts.Dispose();

                if (response == null)
                    return url;

                if (response.IsSuccessStatusCode)
                    break;

                var status = (int)response.StatusCode;
                if (status == 429 || status >= 500)
                {
                    var delay = (attempt + 1) * 2; // 2s, 4s, 6s backoff
                    _logger.LogWarning("Image download returned {Status} for {Url}, retrying in {Delay}s (attempt {Attempt}/{Max})",
                        response.StatusCode, url, delay, attempt + 1, maxRetries);
                    response.Dispose();
                    response = null;
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    continue;
                }

                // Non-retryable error
                _logger.LogWarning("Failed to download image: {Status} from {Url}", response.StatusCode, url);
                response.Dispose();
                return url;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download image after {Max} retries: {Url}", maxRetries, url);
                response?.Dispose();
                return url;
            }

            using var responseScope = response; // Ensure disposal
            
            // Security: Validate content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!ImageFetchPolicy.AllowedContentTypes.Contains(contentType))
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
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var stream = await response.Content.ReadAsStreamAsync(readCts.Token);
            await using var fileStream = File.Create(finalPath);
            
            // Read in chunks, enforcing size limit
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(buffer, readCts.Token)) > 0)
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
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), readCts.Token);
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
