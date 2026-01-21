using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services;

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

        // Fallback: use first album's cover
        var firstAlbum = await _context.MediaItems
            .Where(m => m.ArtistId == artistId && m.Type == MediaType.Album)
            .OrderBy(m => m.Year ?? int.MaxValue)
            .Select(m => new { m.CoverArtPath })
            .FirstOrDefaultAsync();

        return firstAlbum?.CoverArtPath;
    }

    /// <inheritdoc/>
    public async Task<(byte[] Data, string MimeType)?> GetImageBytesAsync(Guid mediaItemId)
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
                // Fallback to first album's cover
                if (string.IsNullOrEmpty(imagePath))
                {
                    var firstAlbum = await _context.MediaItems
                        .Where(m => m.ArtistId == mediaItemId && m.Type == MediaType.Album)
                        .OrderBy(m => m.Year ?? int.MaxValue)
                        .Select(m => new { m.CoverArtPath })
                        .FirstOrDefaultAsync();
                    imagePath = firstAlbum?.CoverArtPath;
                }
                break;

            default:
                imagePath = item.CoverArtPath;
                break;
        }

        if (string.IsNullOrEmpty(imagePath))
            return null;

        // SECURITY: Validate path before file access
        var fullPath = Path.GetFullPath(imagePath);
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

        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath);
            var mimeType = GetMimeType(fullPath);
            return (bytes, mimeType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading image file: {Path}", fullPath);
            return null;
        }
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

        // Cache directory - check both WebRootPath and CurrentDirectory variants
        var cachePath1 = !string.IsNullOrEmpty(_env.WebRootPath) 
            ? Path.GetFullPath(Path.Combine(_env.WebRootPath, "cache", "images"))
            : null;
        var cachePath2 = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "wwwroot", "cache", "images"));

        _logger.LogDebug("IsPathAllowed check: FullPath={FullPath}, CachePath1={CachePath1}, CachePath2={CachePath2}",
            fullPath, cachePath1 ?? "null", cachePath2);

        // Check if path starts with any allowed path
        foreach (var libPath in libraryPaths)
        {
            var normalizedLibPath = Path.GetFullPath(libPath);
            if (fullPath.StartsWith(normalizedLibPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrEmpty(cachePath1) && fullPath.StartsWith(cachePath1, StringComparison.OrdinalIgnoreCase))
            return true;

        if (fullPath.StartsWith(cachePath2, StringComparison.OrdinalIgnoreCase))
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
