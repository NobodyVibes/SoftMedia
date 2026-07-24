using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class TestableMusicScanner : MusicScanner
{
    public TestableMusicScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MusicScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue,
        IWebHostEnvironment env)
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue, env)
    {
    }

    public Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var file = new FileDiscoveryResult(
            fileInfo.FullName,
            fileInfo.Exists ? fileInfo.Length : 0,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);
        return base.ProcessFileAsync(context, file, existing, library, cancellationToken);
    }
}

/// <summary>
/// Covers SR-WI-030 (parent dedup on cache-cold/watcher-path imports), SR-WI-038
/// ("Various Artists" grouping for compilations), and regression-guards the common
/// single-artist path. Tests synthesize minimal real MP3 files (valid MPEG frames +
/// TagLib-written tags) because the scanner reads tags with TagLib.
/// </summary>
public class MusicScannerTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<ILogger<MusicScanner>> _mockLogger = new();
    private readonly Mock<IMediaNotificationService> _mockNotification = new();
    private readonly Mock<IMetadataQueue> _mockQueue = new();
    private readonly Mock<IMediaAnalysisService> _mockMediaAnalysis = new();
    private readonly Mock<IWebHostEnvironment> _mockEnv = new();
    private readonly AppDbContext _dbContext;
    private readonly string _tmpDir;

    public MusicScannerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    private TestableMusicScanner CreateScanner() => new(
        _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
        _mockMediaAnalysis.Object, _mockQueue.Object, _mockEnv.Object);

    private static Library MusicLibrary() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Music",
        Type = LibraryType.Music
    };

    /// <summary>
    /// Writes a minimal but valid MP3 (a few MPEG-1 Layer III frame headers) and stamps
    /// ID3 tags on it via TagLib — the same library the scanner reads with.
    /// </summary>
    private static string CreateMp3(
        string directory,
        string fileName,
        string title,
        string? album = null,
        string[]? performers = null,
        string[]? albumArtists = null)
    {
        var path = Path.Combine(directory, fileName);

        // MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding → 417-byte frames.
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
        {
            for (var i = 0; i < 4; i++)
                fs.Write(frame, 0, frame.Length);
        }

        using var tagFile = TagLib.File.Create(path);
        tagFile.Tag.Title = title;
        if (album != null) tagFile.Tag.Album = album;
        if (performers != null) tagFile.Tag.Performers = performers;
        if (albumArtists != null) tagFile.Tag.AlbumArtists = albumArtists;
        tagFile.Save();

        return path;
    }

    // ─────────────────────────────────────────────── SR-WI-030 watcher-path parent dedup

    [Fact]
    public async Task WatcherPath_SecondTrack_ReusesExistingArtistAndAlbum()
    {
        // The watcher single-file path uses a FRESH scanner whose session caches are
        // empty; the DB double-check must find the existing parents instead of minting
        // duplicate Artist/Album rows (the Lidarr-import duplication bug).
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "Iron Maiden", "Powerslave");
        Directory.CreateDirectory(albumDir);

        var existingArtist = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Title = "Iron Maiden",
            Type = MediaType.Artist,
            Path = albumDir
        };
        var existingAlbum = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Title = "Powerslave",
            Type = MediaType.Album,
            ArtistId = existingArtist.Id,
            Path = albumDir
        };
        _dbContext.MediaItems.Add(existingArtist);
        _dbContext.MediaItems.Add(existingAlbum);
        await _dbContext.SaveChangesAsync();

        var trackPath = CreateMp3(albumDir, "02 - Aces High.mp3", "Aces High",
            album: "Powerslave", performers: new[] { "Iron Maiden" }, albumArtists: new[] { "Iron Maiden" });

        // Fresh scanner = empty caches, exactly like LibraryWatcher's single-file import.
        var scanner = CreateScanner();
        var result = await scanner.ProcessFileAsync(_dbContext, trackPath, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(ScanResult.New, result.Result);
        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Artist));
        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Album));

        var track = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Audio);
        Assert.Equal(existingArtist.Id, track.ArtistId);
        Assert.Equal(existingAlbum.Id, track.AlbumId);
    }

    [Fact]
    public async Task WatcherPath_ArtistLookupIsCaseInsensitive()
    {
        // The session cache is OrdinalIgnoreCase; the DB double-check must match the
        // same identity so a casing difference in tags doesn't mint a duplicate artist.
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "queen", "News of the World");
        Directory.CreateDirectory(albumDir);

        var existingArtist = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Title = "QUEEN",
            Type = MediaType.Artist,
            Path = albumDir
        };
        _dbContext.MediaItems.Add(existingArtist);
        await _dbContext.SaveChangesAsync();

        var trackPath = CreateMp3(albumDir, "01 - We Will Rock You.mp3", "We Will Rock You",
            album: "News of the World", performers: new[] { "Queen" }, albumArtists: new[] { "Queen" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, trackPath, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Artist));
        var track = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Audio);
        Assert.Equal(existingArtist.Id, track.ArtistId);
    }

    // ─────────────────────────────────────────────── SR-WI-038 Various Artists grouping

    [Fact]
    public async Task VaCompilation_NoAlbumArtistTags_GroupsUnderVariousArtists()
    {
        // Same album name, three different performers, no AlbumArtist tag anywhere:
        // must produce ONE "Various Artists" artist and ONE album, not one
        // single-track album per performer.
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "Now That's What I Call Music");
        Directory.CreateDirectory(albumDir);

        var t1 = CreateMp3(albumDir, "01 - Song A.mp3", "Song A", album: "Now Hits", performers: new[] { "Artist One" });
        var t2 = CreateMp3(albumDir, "02 - Song B.mp3", "Song B", album: "Now Hits", performers: new[] { "Artist Two" });
        var t3 = CreateMp3(albumDir, "03 - Song C.mp3", "Song C", album: "Now Hits", performers: new[] { "Artist Three" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t3, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artists = await _dbContext.MediaItems.Where(m => m.Type == MediaType.Artist).ToListAsync();
        var albums = await _dbContext.MediaItems.Where(m => m.Type == MediaType.Album).ToListAsync();
        var tracks = await _dbContext.MediaItems.Where(m => m.Type == MediaType.Audio).ToListAsync();

        var va = Assert.Single(artists);
        Assert.Equal("Various Artists", va.Title);

        var album = Assert.Single(albums);
        Assert.Equal("Now Hits", album.Title);
        Assert.Equal(va.Id, album.ArtistId);

        Assert.Equal(3, tracks.Count);
        Assert.All(tracks, t =>
        {
            Assert.Equal(va.Id, t.ArtistId);
            Assert.Equal(album.Id, t.AlbumId);
        });
    }

    [Fact]
    public async Task ExplicitVaAlbumArtistTag_NormalizesToCanonicalVariousArtists()
    {
        // "VA" (any casing) as the AlbumArtist tag must land under the same canonical
        // "Various Artists" artist as a spelled-out tag would.
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "Compilation");
        Directory.CreateDirectory(albumDir);

        var t1 = CreateMp3(albumDir, "01 - Track.mp3", "Track One",
            album: "Big Comp", performers: new[] { "Someone" }, albumArtists: new[] { "va" });
        var t2 = CreateMp3(albumDir, "02 - Track.mp3", "Track Two",
            album: "Big Comp", performers: new[] { "Someone Else" }, albumArtists: new[] { "Various Artists" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artist = Assert.Single(await _dbContext.MediaItems.Where(m => m.Type == MediaType.Artist).ToListAsync());
        Assert.Equal("Various Artists", artist.Title);
        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Album));
    }

    // ─────────────────────────────────────────────── single-artist path regression guards

    [Fact]
    public async Task SingleArtistAlbum_WithAlbumArtistTag_Unchanged()
    {
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "Rush", "Moving Pictures");
        Directory.CreateDirectory(albumDir);

        var t1 = CreateMp3(albumDir, "01 - Tom Sawyer.mp3", "Tom Sawyer",
            album: "Moving Pictures", performers: new[] { "Rush" }, albumArtists: new[] { "Rush" });
        var t2 = CreateMp3(albumDir, "02 - Red Barchetta.mp3", "Red Barchetta",
            album: "Moving Pictures", performers: new[] { "Rush" }, albumArtists: new[] { "Rush" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artist = Assert.Single(await _dbContext.MediaItems.Where(m => m.Type == MediaType.Artist).ToListAsync());
        Assert.Equal("Rush", artist.Title);
        var album = Assert.Single(await _dbContext.MediaItems.Where(m => m.Type == MediaType.Album).ToListAsync());
        Assert.Equal("Moving Pictures", album.Title);
        Assert.Equal(2, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Audio));
    }

    [Fact]
    public async Task SingleArtistAlbum_PerformerOnlyTags_UsesPerformerNotVa()
    {
        // No AlbumArtist tag but every track shares one performer: the VA probe must
        // conclude "not a compilation" and the artist stays the performer.
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "A Night at the Opera");
        Directory.CreateDirectory(albumDir);

        var t1 = CreateMp3(albumDir, "01.mp3", "Death on Two Legs",
            album: "A Night at the Opera", performers: new[] { "Queen" });
        var t2 = CreateMp3(albumDir, "02.mp3", "Lazing on a Sunday Afternoon",
            album: "A Night at the Opera", performers: new[] { "Queen" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artist = Assert.Single(await _dbContext.MediaItems.Where(m => m.Type == MediaType.Artist).ToListAsync());
        Assert.Equal("Queen", artist.Title);
        Assert.DoesNotContain(await _dbContext.MediaItems.ToListAsync(), m => m.Title == "Various Artists");
    }

    [Fact]
    public async Task MixedAlbumsInOneDirectory_NotTreatedAsCompilation()
    {
        // Different performers AND different album names (a loose-files dump): the VA
        // heuristic requires a shared album, so each track keeps its performer artist.
        var library = MusicLibrary();
        var dumpDir = Path.Combine(_tmpDir, "Downloads");
        Directory.CreateDirectory(dumpDir);

        var t1 = CreateMp3(dumpDir, "a.mp3", "Song A", album: "Album A", performers: new[] { "Artist A" });
        var t2 = CreateMp3(dumpDir, "b.mp3", "Song B", album: "Album B", performers: new[] { "Artist B" });

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artistTitles = await _dbContext.MediaItems
            .Where(m => m.Type == MediaType.Artist)
            .Select(m => m.Title)
            .ToListAsync();
        Assert.Equal(2, artistTitles.Count);
        Assert.Contains("Artist A", artistTitles);
        Assert.Contains("Artist B", artistTitles);
        Assert.DoesNotContain("Various Artists", artistTitles);
    }

    [Fact]
    public async Task WatcherPath_NewTrackInVaDirectory_ReusesVaArtistAndAlbum()
    {
        // A watcher import into an existing VA compilation folder: the probe sees the
        // siblings, resolves "Various Artists", and the DB double-check reuses the
        // existing VA parents instead of duplicating them.
        var library = MusicLibrary();
        var albumDir = Path.Combine(_tmpDir, "Party Comp");
        Directory.CreateDirectory(albumDir);

        var t1 = CreateMp3(albumDir, "01.mp3", "Opener", album: "Party Comp", performers: new[] { "DJ One" });
        var t2 = CreateMp3(albumDir, "02.mp3", "Banger", album: "Party Comp", performers: new[] { "DJ Two" });

        // First scan creates the VA parents.
        var firstScanner = CreateScanner();
        await firstScanner.ProcessFileAsync(_dbContext, t1, null, library, CancellationToken.None);
        await firstScanner.ProcessFileAsync(_dbContext, t2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        // New track lands; the watcher spins up a fresh scanner (empty caches).
        var t3 = CreateMp3(albumDir, "03.mp3", "Closer", album: "Party Comp", performers: new[] { "DJ Three" });
        var watcherScanner = CreateScanner();
        await watcherScanner.ProcessFileAsync(_dbContext, t3, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var artist = Assert.Single(await _dbContext.MediaItems.Where(m => m.Type == MediaType.Artist).ToListAsync());
        Assert.Equal("Various Artists", artist.Title);
        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Album));
        Assert.Equal(3, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Audio));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (Directory.Exists(_tmpDir))
                Directory.Delete(_tmpDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
