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

public class TestableMovieScanner : MovieScanner
{
    public TestableMovieScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MovieScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue,
        ILocalArtworkService localArtwork)
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue, localArtwork)
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
        var fileDiscover = new FileDiscoveryResult(
            fileInfo.FullName,
            fileInfo.Exists ? fileInfo.Length : 0,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);
        return base.ProcessFileAsync(context, fileDiscover, existing, library, cancellationToken);
    }
}

/// <summary>
/// SM-WI-010 — rescans must never wipe enriched Years or revert admin-corrected
/// (MetadataLocked) identity fields. File names are real ones from the operator's
/// library (Fixtures/real-library-manifest.json): "A_Star_is_Born.webm" is a genuine
/// yearless filename, "small.soldiers.1998…" a genuine scene-style name.
/// </summary>
public class MovieScannerIdentityStampingTests : IDisposable
{
    private readonly TestableMovieScanner _scanner;
    private readonly AppDbContext _dbContext;
    private readonly string _tempDir;

    public MovieScannerIdentityStampingTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        var localArtwork = new Mock<ILocalArtworkService>();
        localArtwork
            .Setup(a => a.ApplyLocalArtworkAsync(It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, string[]>?>()))
            .ReturnsAsync(new LocalArtworkResult(Changed: false, LocalPosterRemoved: false));

        _scanner = new TestableMovieScanner(
            new Mock<IServiceScopeFactory>().Object,
            new Mock<ILogger<MovieScanner>>().Object,
            new Mock<IMediaNotificationService>().Object,
            new Mock<IMediaAnalysisService>().Object,
            new Mock<IMetadataQueue>().Object,
            localArtwork.Object);

        _tempDir = Directory.CreateTempSubdirectory("sm-wi-010-").FullName;
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private MediaItem SeedExisting(string path, Action<MediaItem> configure)
    {
        var fileInfo = new FileInfo(path);
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = Guid.NewGuid(),
            Path = fileInfo.FullName,
            Type = MediaType.Movie,
            Size = fileInfo.Length,
            DateModified = fileInfo.LastWriteTimeUtc,
        };
        configure(item);
        _dbContext.MediaItems.Add(item);
        _dbContext.SaveChanges();
        return item;
    }

    [Fact]
    public async Task New_duplicate_file_joins_the_existing_movies_version_group()
    {
        // DV-WI-010 — the second copy of a movie (already persisted sibling) is grouped
        // at scan time; the sibling gets the freshly minted id too.
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var first = CreateFile("Inception (2010) 1080p.mkv");
        var second = CreateFile("Inception (2010) 2160p.mkv");

        await _scanner.ProcessFileAsync(_dbContext, first, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        await _scanner.ProcessFileAsync(_dbContext, second, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var movies = _dbContext.MediaItems.Where(m => m.Type == MediaType.Movie).ToList();
        Assert.Equal(2, movies.Count);
        Assert.NotNull(movies[0].VersionGroupId);
        Assert.Equal(movies[0].VersionGroupId, movies[1].VersionGroupId);
    }

    [Fact]
    public async Task Rescan_preserves_enriched_year_for_yearless_filename()
    {
        // Real yearless filename from the library; provider enrichment set the year.
        var path = CreateFile("A_Star_is_Born.webm");
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var existing = SeedExisting(path, m =>
        {
            m.Title = "A Star Is Born";
            m.Year = 1976;
            m.PosterUrl = "http://example.com/p.jpg";
            m.MetadataHash = "abc";
        });

        await _scanner.ProcessFileAsync(_dbContext, path, existing, library, CancellationToken.None);

        Assert.Equal(1976, existing.Year);
        Assert.Equal("A Star Is Born", existing.Title);
    }

    [Fact]
    public async Task Rescan_preserves_locked_identity_from_fix_match()
    {
        // Admin used Fix-Match: corrected title/year differ from what the filename
        // parses to, and the item is locked. The rescan must not revert either.
        var path = CreateFile("small.soldiers.1998.1080p.bluray.x264-veto.mkv");
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var existing = SeedExisting(path, m =>
        {
            m.Title = "Small Soldiers";
            m.SortTitle = "small soldiers";
            m.Year = 1998;
            m.MetadataLocked = true;
        });

        await _scanner.ProcessFileAsync(_dbContext, path, existing, library, CancellationToken.None);

        Assert.Equal("Small Soldiers", existing.Title);
        Assert.Equal("small soldiers", existing.SortTitle);
        Assert.Equal(1998, existing.Year);
        Assert.True(existing.MetadataLocked);
    }

    [Fact]
    public async Task Rescan_fills_missing_year_from_parse_for_unlocked_item()
    {
        // Legacy row with no year; the filename carries one — fill-only applies it.
        var path = CreateFile("small.soldiers.1998.1080p.bluray.x264-veto.mkv");
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var existing = SeedExisting(path, m =>
        {
            m.Title = "small soldiers";
            m.Year = null;
        });

        await _scanner.ProcessFileAsync(_dbContext, path, existing, library, CancellationToken.None);

        Assert.Equal(1998, existing.Year);
    }

    [Fact]
    public async Task New_file_still_parses_title_and_year_from_filename()
    {
        var path = CreateFile("small.soldiers.1998.1080p.bluray.x264-veto.mkv");
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };

        var result = await _scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(ScanResult.New, result.Result);
        var movie = await _dbContext.MediaItems.SingleAsync();
        Assert.Equal("small soldiers", movie.Title);
        Assert.Equal(1998, movie.Year);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
