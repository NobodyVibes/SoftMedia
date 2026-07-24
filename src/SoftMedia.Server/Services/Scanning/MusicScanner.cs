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

    // SR-WI-038 — canonical artist name for VA compilations, plus the tag spellings that
    // should collapse onto it. A compilation tagged "VA" and one tagged "Various Artists"
    // must land under the same artist row.
    private const string VariousArtistsName = "Various Artists";
    private static readonly HashSet<string> VariousArtistsAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Various Artists", "VA"
    };

    // SR-WI-038 — per-directory VA verdicts. A directory is probed at most once per scanner
    // instance (each directory is walked by a single worker, so races only cost a duplicate
    // probe, never a wrong answer). Cleared at scan start alongside the parent caches.
    private readonly ConcurrentDictionary<string, bool> _vaDirectoryCache = new(StringComparer.OrdinalIgnoreCase);

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
        _vaDirectoryCache.Clear();

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
            // Fast path: unchanged file (same size + mtime) needs no re-tagging — opening
            // every audio file with TagLib on every rescan was the dominant cost of
            // scanning a stable music library. Tags can only differ if the file changed.
            if (existing != null && existing.Size == file.Size && existing.DateModified == file.LastWriteUtc)
            {
                return new ScanOperationResult(ScanResult.Skipped, existing.Id, EnqueueMetadata: false);
            }

            // Parse metadata using TagLib
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            // SR-WI-038 — album-artist resolution. An explicit AlbumArtist tag wins; a VA
            // spelling ("Various Artists"/"VA", any casing) collapses onto the canonical
            // Various Artists artist. When the tag is absent, a per-directory probe detects
            // compilations (same album, differing performers) so they group under Various
            // Artists instead of exploding into one single-track album per performer.
            // The common single-artist path (AlbumArtist present, or all tracks share a
            // performer) is behaviorally unchanged.
            var albumArtistTag = GetFirstOrDefault(tag.AlbumArtists);
            string artistName;
            if (albumArtistTag != null)
            {
                artistName = VariousArtistsAliases.Contains(albumArtistTag)
                    ? VariousArtistsName
                    : albumArtistTag;
            }
            else if (IsVariousArtistsDirectory(Path.GetDirectoryName(filePath)))
            {
                artistName = VariousArtistsName;
            }
            else
            {
                artistName = GetFirstOrDefault(tag.Performers) ?? "Unknown Artist";
            }
            // Multi-disc releases often tag each disc's Album as "Name (CD1)" /
            // "Name - CD 2: subtitle", which would otherwise split one album into
            // several. Normalize to the canonical album name for grouping and keep
            // the disc number it yields so tracks still sort/group per disc.
            var (albumName, discFromTitle) = MusicNaming.NormalizeAlbumName(tag.Album);
            if (string.IsNullOrWhiteSpace(albumName))
                albumName = "Unknown Album";
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
            // Disc number priority: embedded Disc tag → a "(CD2)"-style album-title
            // suffix → a "CD2"/"Disc 2" parent folder. This keeps multi-disc albums
            // whose tags omit the disc number ordered and grouped correctly on the
            // album detail page (the repository orders by DiscNumber then TrackNumber).
            track.DiscNumber = (int?)tag.Disc > 0
                ? (int)tag.Disc
                : (discFromTitle ?? MusicNaming.ParseDiscNumberFromPath(filePath));
            track.Year = (int?)tag.Year > 0 ? (int)tag.Year : null;
            track.Duration = tagFile.Properties.Duration.TotalSeconds;
            track.Size = file.Size;
            track.DateModified = file.LastWriteUtc;

            // Note: Since MetadataJson was dropped, MusicScanner now relies on promoted properties.
            // infers base tag presence by looking at DateAdded and base properties.

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

            // SR-WI-030 — double-check against the DB before creating. The watcher
            // single-file path runs on a fresh scanner whose session caches are empty,
            // so without this every import would mint a duplicate artist row. Matched
            // case-insensitively, the same identity the session cache uses.
            var loweredArtist = artistName.ToLowerInvariant();
            var dbArtist = await context.MediaItems
                .FirstOrDefaultAsync(
                    m => m.LibraryId == library.Id
                         && m.Type == MediaType.Artist
                         && m.Title.ToLower() == loweredArtist,
                    cancellationToken);
            if (dbArtist != null)
            {
                _artistCache.TryAdd(artistName, dbArtist);
                return dbArtist;
            }

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

            // SR-WI-035 — parent-creation saves run inside the parallel directory walk,
            // so they must take the scanner-wide write lock like the base class's
            // end-of-directory saves (SQLite tolerates a single writer).
            await _dbWriteLock.WaitAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _dbWriteLock.Release();
            }

            // Queue for metadata enrichment (image/bio)
            await _metadataQueue.EnqueueMetadataRefreshAsync(artist.Id, LibraryType.Music, libraryId: artist.LibraryId);

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

            // SR-WI-030 — double-check against the DB before creating (watcher path runs
            // with empty session caches; see EnsureArtistAsync). Exact-title match — the
            // album cache key uses default (ordinal) string comparison.
            var dbAlbum = await context.MediaItems
                .FirstOrDefaultAsync(
                    m => m.LibraryId == library.Id
                         && m.Type == MediaType.Album
                         && m.ArtistId == artist.Id
                         && m.Title == albumName,
                    cancellationToken);
            if (dbAlbum != null)
            {
                _albumCache.TryAdd(cacheKey, dbAlbum);
                return dbAlbum;
            }

            // Create new album. For multi-disc releases the track sits in a
            // "CD1"/"Disc 2" subfolder; use the real release folder (its parent) so
            // local cover-art lookup finds the album cover, not a per-disc folder.
            var albumDir = MusicNaming.GetAlbumDirectory(trackPath);
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
                DateModified = DateTime.UtcNow
            };
            // Resolve cover art (priority: local file > embedded > deferred)
            await ResolveAlbumCoverAsync(album, albumDir, tagFile, cancellationToken);

            context.MediaItems.Add(album);

            // SR-WI-035 — same write-lock discipline as EnsureArtistAsync.
            await _dbWriteLock.WaitAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _dbWriteLock.Release();
            }

            // Queue for metadata enrichment
            await _metadataQueue.EnqueueMetadataRefreshAsync(album.Id, LibraryType.Music, libraryId: album.LibraryId);

            // Add to cache for subsequent lookups
            _albumCache.TryAdd(cacheKey, album);

            _logger.LogInformation("[MusicScanner] Created album: {AlbumName} by {ArtistName}",
                albumName, artist.Title);
            return album;
        }
    }

    /// <summary>
    /// SR-WI-038 — cached per-directory Various Artists verdict. Only consulted for tracks
    /// that carry no AlbumArtist tag; well-tagged libraries never pay the probe cost, and
    /// unchanged files short-circuit before artist resolution entirely.
    /// </summary>
    private bool IsVariousArtistsDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return false;
        return _vaDirectoryCache.GetOrAdd(directory, ProbeDirectoryForVariousArtists);
    }

    /// <summary>
    /// Decide whether a directory holds a VA compilation: every readable audio file lacks a
    /// (non-VA) AlbumArtist tag, they all share one non-empty normalized album name, and at
    /// least two distinct performers appear. Anything ambiguous (mixed albums, untagged
    /// albums, an explicit per-artist AlbumArtist, a single performer) is NOT a compilation,
    /// which keeps the common single-artist path's behavior unchanged.
    /// </summary>
    private bool ProbeDirectoryForVariousArtists(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            string? sharedAlbum = null;
            var performers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int probed = 0;

            foreach (var siblingPath in Directory.EnumerateFiles(directory))
            {
                var ext = Path.GetExtension(siblingPath).TrimStart('.').ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var sibling = TagLib.File.Create(siblingPath);
                    var siblingTag = sibling.Tag;

                    var siblingAlbumArtist = GetFirstOrDefault(siblingTag.AlbumArtists);
                    if (siblingAlbumArtist != null && !VariousArtistsAliases.Contains(siblingAlbumArtist))
                        return false; // an explicit per-artist AlbumArtist — trust the tag

                    var (siblingAlbum, _) = MusicNaming.NormalizeAlbumName(siblingTag.Album);
                    if (string.IsNullOrWhiteSpace(siblingAlbum))
                        return false; // untagged album — can't claim a shared compilation

                    if (sharedAlbum == null)
                        sharedAlbum = siblingAlbum;
                    else if (!string.Equals(sharedAlbum, siblingAlbum, StringComparison.OrdinalIgnoreCase))
                        return false; // multiple albums in one folder — not one compilation

                    var performer = GetFirstOrDefault(siblingTag.Performers);
                    if (performer != null)
                        performers.Add(performer);
                    probed++;
                }
                catch
                {
                    // Unreadable sibling (mid-copy, corrupt) carries no signal either way.
                }
            }

            var isVa = probed >= 2 && performers.Count >= 2;
            if (isVa)
            {
                _logger.LogInformation(
                    "[MusicScanner] Directory detected as Various Artists compilation ({Performers} performers): {Directory}",
                    performers.Count, directory);
            }
            return isVa;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MusicScanner] VA probe failed for directory {Directory}; assuming single-artist", directory);
            return false;
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
