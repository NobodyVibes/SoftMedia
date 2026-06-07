using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for resolving and serving music-related images.
/// Implements security validation to prevent path traversal attacks.
/// </summary>
public class MusicImageService : IMusicImageService
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MusicImageService> _logger;

    public MusicImageService(
        AppDbContext context,
        IWebHostEnvironment env,
        ILogger<MusicImageService> logger)
    {
        _context = context;
        _env = env;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> GetTrackCoverPathAsync(Guid trackId)
    {
        var track = await _context.MediaItems
            .Where(m => m.Id == trackId && m.Type == MediaType.Audio)
            .Select(m => new { m.AlbumId })
            .FirstOrDefaultAsync();

        if (track?.AlbumId == null) return null;
        return await GetAlbumCoverPathAsync(track.AlbumId.Value);
    }

    /// <inheritdoc/>
    public async Task<string?> GetAlbumCoverPathAsync(Guid albumId)
    {
        var album = await _context.MediaItems
            .Where(m => m.Id == albumId && m.Type == MediaType.Album)
            .Select(m => new { m.CoverArtPath })
            .FirstOrDefaultAsync();

        return album?.CoverArtPath;
    }

    /// <inheritdoc/>
    public async Task<string?> GetArtistImagePathAsync(Guid artistId)
    {
        var artist = await _context.MediaItems
            .Where(m => m.Id == artistId && m.Type == MediaType.Artist)
            .Select(m => new { m.CoverArtPath })
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(artist?.CoverArtPath))
            return artist.CoverArtPath;

        // Fallback: the EARLIEST album that actually has a cover. Skip cover-less
        // albums (e.g. an early demo/bootleg dated before the first studio album)
        // so the artist isn't left imageless just because its oldest entry has no art.
        return await _context.MediaItems
            .Where(m => m.ArtistId == artistId && m.Type == MediaType.Album
                && m.CoverArtPath != null && m.CoverArtPath != "")
            .OrderBy(m => m.Year ?? int.MaxValue)
            .Select(m => m.CoverArtPath)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc/>
    public async Task<(string Path, string MimeType)?> GetImageInfoAsync(Guid mediaItemId)
    {
        var item = await _context.MediaItems
            .Where(m => m.Id == mediaItemId)
            .Select(m => new { m.Type, m.CoverArtPath, m.AlbumId, m.ArtistId })
            .FirstOrDefaultAsync();

        if (item == null) return null;

        string? imagePath = null;

        // Resolve path based on media type
        switch (item.Type)
        {
            case MediaType.Audio:
                if (item.AlbumId.HasValue)
                    imagePath = await GetAlbumCoverPathAsync(item.AlbumId.Value);
                break;

            case MediaType.Album:
                imagePath = item.CoverArtPath;
                break;

            case MediaType.Artist:
                imagePath = item.CoverArtPath;
                // Fallback to the earliest album that actually HAS a cover — skip
                // cover-less albums so the artist isn't imageless just because its
                // oldest entry (e.g. a demo) has no art.
                if (string.IsNullOrEmpty(imagePath))
                {
                    imagePath = await _context.MediaItems
                        .Where(m => m.ArtistId == mediaItemId && m.Type == MediaType.Album
                            && m.CoverArtPath != null && m.CoverArtPath != "")
                        .OrderBy(m => m.Year ?? int.MaxValue)
                        .Select(m => m.CoverArtPath)
                        .FirstOrDefaultAsync();
                }
                break;

            default:
                imagePath = item.CoverArtPath;
                break;
        }

        if (string.IsNullOrEmpty(imagePath))
            return null;

        // SECURITY: Validate path before file access
        var fullPath = ResolveToFileSystemPath(imagePath);
        if (!IsPathAllowed(fullPath))
        {
            _logger.LogWarning("Blocked path traversal attempt for media {MediaId}: {Path}",
                mediaItemId, imagePath);
            return null;
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("Image file not found for media {MediaId}: {Path}",
                mediaItemId, fullPath);
            return null;
        }

        return (fullPath, GetMimeType(fullPath));
    }

    /// <inheritdoc/>
    public async Task<(byte[] Data, string MimeType)?> GetImageBytesAsync(Guid mediaItemId)
    {
        var info = await GetImageInfoAsync(mediaItemId);
        if (info == null) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(info.Value.Path);
            return (bytes, info.Value.MimeType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading image file: {Path}", info.Value.Path);
            return null;
        }
    }

    /// <summary>
    /// CoverArtPath is stored in one of two formats: a WEB-relative URL
    /// ("/cache/images/music/{id}_cover.jpg") for art fetched by the metadata
    /// pipeline (ImageCacheService returns the web URL), or an ABSOLUTE filesystem
    /// path for local-folder / embedded-tag art written by MusicScanner.
    /// Web-relative paths MUST be rebased onto wwwroot before file access — on
    /// Windows Path.GetFullPath("/cache/...") resolves against the current drive
    /// root (C:\cache\...) and misses the real file under wwwroot, so the cover
    /// 404s. Mirrors AudioController.GetCoverArt's TrimStart + Combine.
    /// </summary>
    private string ResolveToFileSystemPath(string imagePath)
    {
        if (imagePath.StartsWith('/') || imagePath.StartsWith('\\'))
        {
            var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
                ? _env.WebRootPath
                : Path.Combine(Environment.CurrentDirectory, "wwwroot");
            return Path.GetFullPath(Path.Combine(webRoot, imagePath.TrimStart('/', '\\')));
        }
        return Path.GetFullPath(imagePath);
    }

    /// <summary>
    /// Validate that a file path is within allowed directories.
    /// Prevents path traversal attacks.
    /// </summary>
    private bool IsPathAllowed(string fullPath)
    {
        // Get all library paths - select libraries then flatten in memory
        var libraries = _context.Libraries.ToList();
        var libraryPaths = libraries.SelectMany(l => l.Paths ?? new List<string>()).ToList();

        // Cache directory — use WebRootPath for reliable path resolution
        var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
            ? _env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        var cachePath1 = Path.GetFullPath(Path.Combine(webRoot, "cache", "images"));

        _logger.LogDebug("IsPathAllowed check: FullPath={FullPath}, CachePath={CachePath}",
            fullPath, cachePath1);

        // Check if path starts with any allowed path
        foreach (var libPath in libraryPaths)
        {
            var normalizedLibPath = Path.GetFullPath(libPath);
            if (fullPath.StartsWith(normalizedLibPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (fullPath.StartsWith(cachePath1, StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogWarning("Path not in allowed directories: {Path}", fullPath);
        return false;
    }

    /// <summary>
    /// Get MIME type from file extension.
    /// </summary>
    private static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }
}
