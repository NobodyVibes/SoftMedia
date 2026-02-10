using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using TagLib;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for music libraries. Handles artist/album/track hierarchy.
/// </summary>
public class MusicScanner : BaseMediaScanner
{
    private readonly IBackgroundImageCacheService _backgroundImageCache;
    private readonly IMediaAnalysisService _mediaAnalysisService;

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

    // Supported audio extensions
    private static readonly string[] AudioExtensions =
    {
        "mp3", "flac", "aac", "m4a", "ogg", "wma", "wav", "ape", "alac"
    };

    public override LibraryType SupportedType => LibraryType.Music;
    public override string[] SupportedExtensions => AudioExtensions;
    public override string DisplayName => "Music Scanner";

    public MusicScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MusicScanner> logger,
        IMediaNotificationService notificationService,
        IBackgroundImageCacheService backgroundImageCache,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _backgroundImageCache = backgroundImageCache;
        _mediaAnalysisService = mediaAnalysisService;
    }

    /// <summary>
    /// Override to clear session caches at start of scan.
    /// </summary>
    public override async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // No session caches anymore
        await base.ScanLibraryAsync(library, progress, cancellationToken);
    }

    /// <summary>
    /// Process a single audio file.
    /// </summary>
    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
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
            track.Size = new FileInfo(filePath).Length;
            track.DateModified = System.IO.File.GetLastWriteTimeUtc(filePath);

            // Store metadata for frontend display (artist name, album name, genre)
            var metadata = new Dictionary<string, object>
            {
                { "artist", artistName },
                { "album", albumName }
            };
            if (!string.IsNullOrEmpty(tag.FirstGenre))
            {
                metadata["genre"] = tag.FirstGenre;
            }
            if (tag.AlbumArtists.Length > 0 && tag.AlbumArtists[0] != artistName)
            {
                metadata["albumArtist"] = tag.AlbumArtists[0];
            }
            track.MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);

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
    /// Get or create an artist entity.
    /// </summary>
    private async Task<MediaItem> EnsureArtistAsync(
        AppDbContext context,
        string artistName,
        Library library,
        string trackPath,
        CancellationToken cancellationToken)
    {
        using (await LockParentAsync(artistName, cancellationToken))
        {
            // Check database
            var artist = await context.MediaItems
                .AsNoTracking()
                .FirstOrDefaultAsync(m =>
                    m.Title == artistName &&
                    m.Type == MediaType.Artist &&
                    m.LibraryId == library.Id,
                    cancellationToken);

            if (artist != null)
                return artist;

            // Create new artist
            artist = new MediaItem
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

            _logger.LogInformation("[MusicScanner] Created artist: {ArtistName}", artistName);

            return artist;
        }
    }

    /// <summary>
    /// Get or create an album entity.
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
        var key = $"{artist.Id}-{albumName}";
        
        using (await LockParentAsync(key, cancellationToken))
        {
            // Check database
            var album = await context.MediaItems
                .AsNoTracking()
                .FirstOrDefaultAsync(m =>
                    m.Title == albumName &&
                    m.ArtistId == artist.Id &&
                    m.Type == MediaType.Album &&
                    m.LibraryId == library.Id,
                    cancellationToken);

            if (album != null)
                return album;

            // Create new album
            var albumDir = Path.GetDirectoryName(trackPath) ?? trackPath;
            album = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Title = albumName,
                SortTitle = MediaStringHelpers.GetSortTitle(albumName),
                Path = albumDir,
                Type = MediaType.Album,
                ArtistId = artist.Id,
                Year = (int?)tagFile.Tag.Year > 0 ? (int)tagFile.Tag.Year : null,
                DateModified = DateTime.UtcNow
            };

            // Resolve cover art (priority: local file > embedded > deferred)
            await ResolveAlbumCoverAsync(album, albumDir, tagFile, cancellationToken);

            context.MediaItems.Add(album);
            await context.SaveChangesAsync(cancellationToken);
            
            // Queue for metadata enrichment if we didn't find local cover, or just always for better metadata?
            // Let's queue always for consistency.
            await _metadataQueue.EnqueueMetadataRefreshAsync(album.Id, LibraryType.Music);

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
            var cacheDir = Path.Combine(
                Environment.CurrentDirectory,
                "wwwroot", "cache", "images", "music");
            Directory.CreateDirectory(cacheDir);

            var extension = GetImageExtension(coverPic.MimeType);
            var cachePath = Path.Combine(cacheDir, $"{album.Id}{extension}");

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

        // 3. Queue for background fetch (MusicBrainz)
        // Will be handled after SaveChangesAsync when the album has a valid ID
        _backgroundImageCache.QueueImageCaching(album.Id);
        _logger.LogInformation("[MusicScanner] Queued album for background cover fetch: {Album}", album.Title);
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
