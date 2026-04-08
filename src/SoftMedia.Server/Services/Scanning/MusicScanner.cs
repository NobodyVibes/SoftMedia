using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using Microsoft.AspNetCore.Hosting;
using TagLib;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for music libraries. Handles artist/album/track hierarchy.
/// </summary>
public class MusicScanner : BaseMediaScanner
{
    private readonly IMediaAnalysisService _mediaAnalysisService;
    private readonly IWebHostEnvironment _env;

    // Session caches — pre-loaded at scan start, used for O(1) lookups during parallel directory processing
    private readonly ConcurrentDictionary<string, MediaItem> _artistCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Guid ArtistId, string AlbumName), MediaItem> _albumCache = new();

    // Local cover art filenames to check (in priority order)
    private static readonly string[] LocalCoverNames =
    {
        "cover.jpg", "cover.png", "cover.webp",
        "folder.jpg", "folder.png",
        "album.jpg", "album.png",
        "front.jpg", "front.png"
    };

    // Subdirectories to check for cover art (in priority order)
    private static readonly string[] CoverSubdirectories =
    {
        "Covers", "Cover", "Artwork", "Art", "Scans", "Images", "CD"
    };

    public override LibraryType SupportedType => LibraryType.Music;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Audio;
    public override string DisplayName => "Music Scanner";

    public MusicScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MusicScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue,
        IWebHostEnvironment env)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
        _env = env;
    }

    /// <summary>
    /// Override to pre-load session caches before the parallel directory loop.
    /// Bulk-loads all existing Artist and Album items for this library in one query each,
    /// eliminating the N+1 pattern where each file would trigger a separate DB lookup.
    /// </summary>
    public override async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Clear session caches
        _artistCache.Clear();
        _albumCache.Clear();

        // Bulk pre-load all existing Artists and Albums for this library
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingArtists = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Artist)
                .ToListAsync(cancellationToken);

            foreach (var a in existingArtists)
                _artistCache.TryAdd(a.Title, a);

            var existingAlbums = await context.MediaItems
                .AsNoTracking()
                .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Album)
                .ToListAsync(cancellationToken);

            foreach (var a in existingAlbums)
            {
                if (a.ArtistId.HasValue)
                    _albumCache.TryAdd((a.ArtistId.Value, a.Title), a);
            }

            _logger.LogInformation("[MusicScanner] Pre-loaded {ArtistCount} artists and {AlbumCount} albums for library {LibraryId}",
                existingArtists.Count, existingAlbums.Count, library.Id);
        }

        await base.ScanLibraryAsync(library, progress, cancellationToken);
    }

    /// <summary>
    /// Process a single audio file.
    /// </summary>
    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            // Parse metadata using TagLib
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            var artistName = GetFirstOrDefault(tag.AlbumArtists) 
                ?? GetFirstOrDefault(tag.Performers) 
                ?? "Unknown Artist";
            var albumName = tag.Album ?? "Unknown Album";
            var trackTitle = tag.Title ?? Path.GetFileNameWithoutExtension(filePath);

            // Get or create artist (Thread Safe)
            var artist = await EnsureArtistAsync(context, artistName, library, filePath, cancellationToken);

            // Get or create album (Thread Safe)
            var album = await EnsureAlbumAsync(context, albumName, artist, library, filePath, tagFile, cancellationToken);

            // Create or update track
            var isNew = existing == null;
            var track = existing ?? new MediaItem { LibraryId = library.Id };

            track.Title = trackTitle;
            track.SortTitle = MediaStringHelpers.GetSortTitle(trackTitle);
            track.Path = filePath;
            track.Type = MediaType.Audio;
            track.ArtistId = artist.Id;
            track.AlbumId = album.Id;
            track.TrackNumber = (int?)tag.Track > 0 ? (int)tag.Track : null;
            track.DiscNumber = (int?)tag.Disc > 0 ? (int)tag.Disc : null;
            track.Year = (int?)tag.Year > 0 ? (int)tag.Year : null;
            track.Duration = tagFile.Properties.Duration.TotalSeconds;
            track.Size = file.Size;
            track.DateModified = file.LastWriteUtc;

            // Store metadata for frontend display and to signal EmbeddedMusicProvider
            // that tags have already been read (avoids redundant TagLib.File.Create).
            var metadataResult = new MetadataResult
            {
                Artist = artistName,
                Album = albumName,
                Title = trackTitle,
                Duration = tagFile.Properties.Duration.TotalSeconds,
                HasEmbeddedArt = tagFile.Tag.Pictures.Length > 0,
                Extra = new Dictionary<string, System.Text.Json.JsonElement>()
            };

            metadataResult.Extra["scannedTags"] = System.Text.Json.JsonSerializer.SerializeToElement(true);
            
            if (tag.TrackCount > 0)
            {
                metadataResult.Extra["totalTracks"] = System.Text.Json.JsonSerializer.SerializeToElement((int)tag.TrackCount);
            }
            if (tag.Genres.Length > 0)
            {
                metadataResult.Genres = tag.Genres.ToList();
            }
            else if (!string.IsNullOrEmpty(tag.FirstGenre))
            {
                metadataResult.Genres = new List<string> { tag.FirstGenre };
            }
            
            if (tag.AlbumArtists.Length > 0 && tag.AlbumArtists[0] != artistName)
            {
                metadataResult.Extra["albumArtist"] = System.Text.Json.JsonSerializer.SerializeToElement(tag.AlbumArtists[0]);
            }

            track.MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadataResult);

            // Audio codec info
            track.AudioCodec = tagFile.Properties.Codecs
                .FirstOrDefault(c => c is TagLib.ICodec)?.Description ?? "Unknown";

            if (isNew)
            {
                context.MediaItems.Add(track);
                _logger.LogDebug("[MusicScanner] Added track: {Title} by {Artist}",
                    track.Title, artistName);
                return new ScanOperationResult(ScanResult.New, track.Id, EnqueueMetadata: false);
            }
            else
            {
                _logger.LogDebug("[MusicScanner] Updated track: {Title}", track.Title);
                return new ScanOperationResult(ScanResult.Updated, track.Id, EnqueueMetadata: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MusicScanner] Error processing audio file: {FilePath}", filePath);
            return new ScanOperationResult(ScanResult.Skipped);
        }
    }

    /// <summary>
    /// Get or create an artist entity. Uses pre-loaded cache for O(1) lookups,
    /// falling back to DB + lock only when creating new artists.
    /// </summary>
    private async Task<MediaItem> EnsureArtistAsync(
        AppDbContext context,
        string artistName,
        Library library,
        string trackPath,
        CancellationToken cancellationToken)
    {
        // Fast path: check pre-loaded cache (thread-safe ConcurrentDictionary)
        if (_artistCache.TryGetValue(artistName, out var cached))
            return cached;

        // Slow path: cache miss — acquire lock and create new artist
        using (await LockParentAsync(artistName, cancellationToken))
        {
            // Double-check cache after acquiring lock
            if (_artistCache.TryGetValue(artistName, out cached))
                return cached;

            // Create new artist
            var artist = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Title = artistName,
                SortTitle = MediaStringHelpers.GetSortTitle(artistName),
                Path = Path.GetDirectoryName(trackPath) ?? trackPath,
                Type = MediaType.Artist,
                DateModified = DateTime.UtcNow
            };

            // Check for local artist image
            var artistDir = Path.GetDirectoryName(trackPath);
            if (artistDir != null)
            {
                var parentDir = Path.GetDirectoryName(artistDir);
                if (parentDir != null)
                {
                    var artistImage = FindArtistImage(parentDir);
                    if (artistImage != null)
                    {
                        artist.CoverArtPath = artistImage;
                        _logger.LogDebug("[MusicScanner] Found artist image: {Path}", artistImage);
                    }
                }
            }

            context.MediaItems.Add(artist);
            await context.SaveChangesAsync(cancellationToken);

            // Queue for metadata enrichment (image/bio)
            await _metadataQueue.EnqueueMetadataRefreshAsync(artist.Id, LibraryType.Music);

            // Add to cache for subsequent lookups
            _artistCache.TryAdd(artistName, artist);

            _logger.LogInformation("[MusicScanner] Created artist: {ArtistName}", artistName);
            return artist;
        }
    }

    /// <summary>
    /// Get or create an album entity. Uses pre-loaded cache for O(1) lookups,
    /// falling back to DB + lock only when creating new albums.
    /// </summary>
    private async Task<MediaItem> EnsureAlbumAsync(
        AppDbContext context,
        string albumName,
        MediaItem artist,
        Library library,
        string trackPath,
        TagLib.File tagFile,
        CancellationToken cancellationToken)
    {
        var cacheKey = (artist.Id, albumName);

        // Fast path: check pre-loaded cache
        if (_albumCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Slow path: cache miss — acquire lock and create new album
        var lockKey = $"{artist.Id}-{albumName}";
        using (await LockParentAsync(lockKey, cancellationToken))
        {
            // Double-check cache after acquiring lock
            if (_albumCache.TryGetValue(cacheKey, out cached))
                return cached;

            // Create new album
            var albumDir = Path.GetDirectoryName(trackPath) ?? trackPath;
            var album = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Title = albumName,
                SortTitle = MediaStringHelpers.GetSortTitle(albumName),
                Path = albumDir,
                Type = MediaType.Album,
                ArtistId = artist.Id,
                Year = (int?)tagFile.Tag.Year > 0 ? (int)tagFile.Tag.Year : null,
                DateModified = DateTime.UtcNow,
                // Seed artist context so metadata providers can search accurately
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { artist = artist.Title })
            };

            // Resolve cover art (priority: local file > embedded > deferred)
            await ResolveAlbumCoverAsync(album, albumDir, tagFile, cancellationToken);

            context.MediaItems.Add(album);
            await context.SaveChangesAsync(cancellationToken);

            // Queue for metadata enrichment
            await _metadataQueue.EnqueueMetadataRefreshAsync(album.Id, LibraryType.Music);

            // Add to cache for subsequent lookups
            _albumCache.TryAdd(cacheKey, album);

            _logger.LogInformation("[MusicScanner] Created album: {AlbumName} by {ArtistName}",
                albumName, artist.Title);
            return album;
        }
    }

    /// <summary>
    /// Resolve album cover art from local files, embedded art, or queue for remote fetch.
    /// </summary>
    private async Task ResolveAlbumCoverAsync(
        MediaItem album,
        string albumDir,
        TagLib.File tagFile,
        CancellationToken cancellationToken)
    {
        // 1. Check for local cover art
        var localCover = FindLocalCoverArt(albumDir);
        if (localCover != null)
        {
            album.CoverArtPath = localCover;
            _logger.LogInformation("[MusicScanner] Found local cover for {Album}: {Path}", album.Title, localCover);
            return;
        }

        // 2. Try to extract embedded cover
        var pictures = tagFile.Tag.Pictures;
        _logger.LogInformation("[MusicScanner] Album {Album} has {Count} embedded pictures", album.Title, pictures.Length);
        
        if (pictures.Length > 0)
        {
            var coverPic = pictures.FirstOrDefault(p =>
                p.Type == PictureType.FrontCover ||
                p.Type == PictureType.Other) ?? pictures[0];

            // Extract to cache
            var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
                ? _env.WebRootPath
                : Path.Combine(Environment.CurrentDirectory, "wwwroot");
            var cacheDir = Path.Combine(webRoot, "cache", "images", "music");
            Directory.CreateDirectory(cacheDir);

            var extension = GetImageExtension(coverPic.MimeType);
            var cachePath = Path.Combine(cacheDir, $"{album.Id}_cover{extension}");

            try
            {
                await System.IO.File.WriteAllBytesAsync(cachePath, coverPic.Data.Data, cancellationToken);
                album.CoverArtPath = cachePath;
                _logger.LogInformation("[MusicScanner] Extracted embedded cover for {Album}: {Path}", album.Title, cachePath);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MusicScanner] Failed to extract embedded cover for album {Album}",
                    album.Title);
            }
        }

        // 3. No local or embedded art found — metadata pipeline will handle image
        // download via MetadataAggregator → ImageUrlExtractorService after enrichment
        _logger.LogInformation("[MusicScanner] No local cover found for {Album}, will be handled by metadata pipeline", album.Title);
    }

    /// <summary>
    /// Find local cover art file in the album directory or common subdirectories.
    /// </summary>
    private string? FindLocalCoverArt(string albumDirectory)
    {
        if (!Directory.Exists(albumDirectory))
            return null;

        // First, check the album directory itself
        var result = SearchDirectoryForCover(albumDirectory);
        if (result != null)
            return result;

        // Then check common subdirectories
        foreach (var subdir in CoverSubdirectories)
        {
            var subdirPath = Path.Combine(albumDirectory, subdir);
            if (Directory.Exists(subdirPath))
            {
                result = SearchDirectoryForCover(subdirPath);
                if (result != null)
                    return result;
            }
            
            // Also try case-insensitive match for the subdirectory on Windows
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var matchingDirs = Directory.GetDirectories(albumDirectory, subdir, SearchOption.TopDirectoryOnly);
                    foreach (var matchedDir in matchingDirs)
                    {
                        result = SearchDirectoryForCover(matchedDir);
                        if (result != null)
                            return result;
                    }
                }
                catch { /* Ignore access errors */ }
            }
        }

        return null;
    }

    /// <summary>
    /// Search a single directory for cover art files.
    /// </summary>
    private string? SearchDirectoryForCover(string directory)
    {
        foreach (var name in LocalCoverNames)
        {
            var path = Path.Combine(directory, name);
            if (System.IO.File.Exists(path))
                return path;

            // Case-insensitive check on Windows
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var files = Directory.GetFiles(directory, name, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        return files[0];
                }
                catch { /* Ignore access errors */ }
            }
        }

        return null;
    }

    /// <summary>
    /// Find artist image in the artist directory.
    /// </summary>
    private string? FindArtistImage(string artistDirectory)
    {
        if (!Directory.Exists(artistDirectory))
            return null;

        var artistImages = new[] { "artist.jpg", "artist.png", "folder.jpg" };
        foreach (var name in artistImages)
        {
            var path = Path.Combine(artistDirectory, name);
            if (System.IO.File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Cleanup empty albums and artists.
    /// </summary>
    protected override async Task CleanupEmptyContainersAsync(
        AppDbContext context,
        Library library,
        CancellationToken cancellationToken)
    {
        // Find albums with no tracks
        var emptyAlbums = await context.MediaItems
            .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Album)
            .Where(album => !context.MediaItems.Any(t =>
                t.Type == MediaType.Audio && t.AlbumId == album.Id))
            .ToListAsync(cancellationToken);

        if (emptyAlbums.Count > 0)
        {
            _logger.LogInformation("[MusicScanner] Removing {Count} empty albums", emptyAlbums.Count);
            context.MediaItems.RemoveRange(emptyAlbums);
        }

        // Find artists with no tracks
        var emptyArtists = await context.MediaItems
            .Where(m => m.LibraryId == library.Id && m.Type == MediaType.Artist)
            .Where(artist => !context.MediaItems.Any(t =>
                t.Type == MediaType.Audio && t.ArtistId == artist.Id))
            .ToListAsync(cancellationToken);

        if (emptyArtists.Count > 0)
        {
            _logger.LogInformation("[MusicScanner] Removing {Count} empty artists", emptyArtists.Count);
            context.MediaItems.RemoveRange(emptyArtists);
        }
    }

    /// <summary>
    /// Get image file extension from MIME type.
    /// </summary>
    private static string GetImageExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg"
    };

    /// <summary>
    /// Get first non-null value from array or null.
    /// </summary>
    private static string? GetFirstOrDefault(string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

// End of class
}
