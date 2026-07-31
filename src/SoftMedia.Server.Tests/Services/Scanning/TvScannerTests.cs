using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class TestableTvScanner : TvScanner
{
    public TestableTvScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<TvScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue)
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

    public void SeedSeriesMetadata(Guid seriesId, Dictionary<string, object>? metadata)
        => SeedParsedSeriesMetadata(seriesId, metadata);
}

public class TvScannerTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILogger<TvScanner>> _mockLogger;
    private readonly Mock<IMediaNotificationService> _mockNotification;
    private readonly Mock<IMetadataQueue> _mockQueue;
    private readonly Mock<IMediaAnalysisService> _mockMediaAnalysis;
    private readonly AppDbContext _dbContext;
    private readonly string _tmpDir;

    public TvScannerTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockLogger = new Mock<ILogger<TvScanner>>();
        _mockNotification = new Mock<IMediaNotificationService>();
        _mockQueue = new Mock<IMetadataQueue>();
        _mockMediaAnalysis = new Mock<IMediaAnalysisService>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _tmpDir = Path.Combine(Path.GetTempPath(), "tvscanner-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
    }

    private TestableTvScanner CreateScanner() => new(
        _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
        _mockMediaAnalysis.Object, _mockQueue.Object);

    private string CreateFile(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { _tmpDir }.Concat(relativeParts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "dummy video content");
        return path;
    }

    // ─────────────────────────────────────────────── DV-WI-010: version groups

    [Fact]
    public async Task ProcessFileAsync_DuplicateEpisodeFiles_ShareTheDeterministicVersionGroup()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var f1 = CreateFile("Show", "Season 1", "Show S01E03.mkv");
        var f2 = CreateFile("Show", "Season 1", "Show S01E03 2160p.mkv");

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, f1, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        await scanner.ProcessFileAsync(_dbContext, f2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var episodes = _dbContext.MediaItems.Where(m => m.Type == MediaType.Episode).ToList();
        Assert.Equal(2, episodes.Count);
        Assert.NotNull(episodes[0].VersionGroupId);
        Assert.Equal(episodes[0].VersionGroupId, episodes[1].VersionGroupId);
        // Deterministic: any worker (or the boot backfill) computes this exact id.
        Assert.Equal(
            VersionGroupHelper.ComputeEpisodeGroupId(episodes[0].SeriesId!.Value, 1, 3),
            episodes[0].VersionGroupId);
    }

    [Fact]
    public async Task ProcessFileAsync_AdminSplitGroup_SurvivesARescanOfTheSameFile()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var f1 = CreateFile("Show", "Season 1", "Show S01E03.mkv");
        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, f1, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        // Admin split: fresh random id replaces the deterministic one.
        var episode = _dbContext.MediaItems.Single(m => m.Type == MediaType.Episode);
        var splitGroup = Guid.NewGuid();
        episode.VersionGroupId = splitGroup;
        await _dbContext.SaveChangesAsync();

        // Rescan of the SAME file (identity unchanged) must not reclaim it.
        await scanner.ProcessFileAsync(_dbContext, f1, episode, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(splitGroup, _dbContext.MediaItems.Single(m => m.Type == MediaType.Episode).VersionGroupId);
    }

    // ─────────────────────────────────────────────── SR-WI-030: watcher-path parent reuse

    [Fact]
    public async Task ProcessFileAsync_FreshScanner_ReusesExistingSeriesAndSeason()
    {
        // First episode processed by one scanner instance (creates series + season).
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var ep1 = CreateFile("Doctor Who", "Season 1", "Doctor Who S01E01.mkv");
        var ep2 = CreateFile("Doctor Who", "Season 1", "Doctor Who S01E02.mkv");

        var scannerA = CreateScanner();
        await scannerA.ProcessFileAsync(_dbContext, ep1, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        // Second episode arrives on the watcher path: a FRESH scanner instance whose
        // session caches are empty. The DB double-check must find the existing parents
        // instead of minting duplicates (the pre-fix Sonarr-import bug).
        var scannerB = CreateScanner();
        await scannerB.ProcessFileAsync(_dbContext, ep2, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Series));
        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Season));
        Assert.Equal(2, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Episode));

        var series = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Series);
        var episodes = await _dbContext.MediaItems.Where(m => m.Type == MediaType.Episode).ToListAsync();
        Assert.All(episodes, e => Assert.Equal(series.Id, e.SeriesId));
    }

    [Fact]
    public async Task ProcessFileAsync_WatcherCreatedSeries_EnqueuesMetadataImmediately()
    {
        // Outside a full scan there is no post-scan enrichment drain, so a series created
        // on the single-file path must enqueue metadata directly (SR-WI-030 second half).
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var ep = CreateFile("New Show", "Season 1", "New Show S01E01.mkv");

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, ep, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var series = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Series);
        _mockQueue.Verify(q => q.EnqueueMetadataRefreshAsync(
                series.Id, LibraryType.TV, It.IsAny<bool>(), It.IsAny<int>(), library.Id),
            Times.AtLeastOnce);
    }

    // ─────────────────────────────────────────────── SR-WI-034: (title, year) identity

    [Fact]
    public async Task ProcessFileAsync_SameTitleDifferentYear_CreatesSeparateSeries()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var epOld = CreateFile("Doctor Who 1963", "Doctor Who 1963 S01E01.mkv");
        var epNew = CreateFile("Doctor Who 2005", "Doctor Who 2005 S01E01.mkv");

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, epOld, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, epNew, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var series = await _dbContext.MediaItems
            .Where(m => m.Type == MediaType.Series)
            .OrderBy(m => m.Year)
            .ToListAsync();

        Assert.Equal(2, series.Count);
        Assert.All(series, s => Assert.Equal("Doctor Who", s.Title));
        Assert.Equal(1963, series[0].Year);
        Assert.Equal(2005, series[1].Year);
    }

    [Fact]
    public async Task ProcessFileAsync_NoYearInName_ReusesExistingTitledSeries()
    {
        // Null year is a wildcard: a file without a year must not spawn a duplicate of an
        // existing same-title series (the conservative choice — no automatic splits).
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var withYear = CreateFile("Doctor Who 2005", "Doctor Who 2005 S01E01.mkv");
        var withoutYear = CreateFile("Doctor Who 2005", "Doctor Who S01E02.mkv");

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, withYear, null, library, CancellationToken.None);
        await scanner.ProcessFileAsync(_dbContext, withoutYear, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(1, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Series));
        Assert.Equal(2, await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.Episode));
    }

    // ─────────────────────────────────────────────── SR-WI-033: Specials directory parsing

    [Fact]
    public async Task ProcessFileAsync_SpecialsFolder_LandsInSeasonZeroOfParentShow()
    {
        // Filename carries no show name or episode markers; before the fix this created a
        // series literally named "Specials".
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var special = CreateFile("Doctor Who", "Specials", "The Christmas Invasion.mkv");

        var scanner = CreateScanner();
        await scanner.ProcessFileAsync(_dbContext, special, null, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var series = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Series);
        Assert.Equal("Doctor Who", series.Title);
        Assert.False(await _dbContext.MediaItems.AnyAsync(m => m.Type == MediaType.Series && m.Title == "Specials"));

        var season = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Season);
        Assert.Equal(0, season.SeasonNumber);
        Assert.Equal(series.Id, season.SeriesId);

        var episode = await _dbContext.MediaItems.SingleAsync(m => m.Type == MediaType.Episode);
        Assert.Equal(0, episode.SeasonNumber);
        Assert.Equal(series.Id, episode.SeriesId);
    }

    // ─────────────────────────────────────────────── SR-WI-031: backdrop clobber guard

    private const string TvMazePayload = """
        {"_embedded":{"episodes":[{"season":1,"number":1,"name":"Rose",
        "summary":"<p>The Doctor meets Rose.</p>","airdate":"2005-03-26",
        "image":{"original":"https://static.tvmaze.com/ep1.jpg","medium":"https://static.tvmaze.com/ep1-med.jpg"}}]}}
        """;

    private async Task<(MediaItem Series, MediaItem Episode)> SeedSeriesWithEpisodeAsync(
        Library library, string episodePath, string? backdropUrl)
    {
        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Title = "Doctor Who",
            Type = MediaType.Series,
            Path = Path.GetDirectoryName(episodePath)!
        };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            SeriesId = series.Id,
            Title = "Rose",
            Type = MediaType.Episode,
            Path = episodePath,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            BackdropUrl = backdropUrl
        };
        _dbContext.MediaItems.AddRange(series, episode);
        await _dbContext.SaveChangesAsync();
        return (series, episode);
    }

    [Fact]
    public async Task ProcessFileAsync_Rescan_PreservesLocallyCachedBackdrop()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var epPath = CreateFile("Doctor Who", "Doctor Who S01E01.mkv");
        var (series, episode) = await SeedSeriesWithEpisodeAsync(
            library, epPath, backdropUrl: "/cache/images/still-abc123.jpg");

        var scanner = CreateScanner();
        scanner.SeedSeriesMetadata(series.Id,
            JsonSerializer.Deserialize<Dictionary<string, object>>(TvMazePayload));

        await scanner.ProcessFileAsync(_dbContext, epPath, episode, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var reloaded = await _dbContext.MediaItems.SingleAsync(m => m.Id == episode.Id);
        Assert.Equal("/cache/images/still-abc123.jpg", reloaded.BackdropUrl);
    }

    [Fact]
    public async Task ProcessFileAsync_EmptyBackdrop_StillGetsProviderUrl()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var epPath = CreateFile("Doctor Who", "Doctor Who S01E01.mkv");
        var (series, episode) = await SeedSeriesWithEpisodeAsync(library, epPath, backdropUrl: null);

        var scanner = CreateScanner();
        scanner.SeedSeriesMetadata(series.Id,
            JsonSerializer.Deserialize<Dictionary<string, object>>(TvMazePayload));

        await scanner.ProcessFileAsync(_dbContext, epPath, episode, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var reloaded = await _dbContext.MediaItems.SingleAsync(m => m.Id == episode.Id);
        Assert.Equal("https://static.tvmaze.com/ep1.jpg", reloaded.BackdropUrl);
    }

    [Fact]
    public async Task ProcessFileAsync_RemoteBackdrop_IsRefreshedFromProvider()
    {
        // A stale remote URL (not locally cached) may still be replaced by the provider's
        // current URL — the guard only protects /cache/ paths.
        var library = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var epPath = CreateFile("Doctor Who", "Doctor Who S01E01.mkv");
        var (series, episode) = await SeedSeriesWithEpisodeAsync(
            library, epPath, backdropUrl: "https://static.tvmaze.com/old-url.jpg");

        var scanner = CreateScanner();
        scanner.SeedSeriesMetadata(series.Id,
            JsonSerializer.Deserialize<Dictionary<string, object>>(TvMazePayload));

        await scanner.ProcessFileAsync(_dbContext, epPath, episode, library, CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var reloaded = await _dbContext.MediaItems.SingleAsync(m => m.Id == episode.Id);
        Assert.Equal("https://static.tvmaze.com/ep1.jpg", reloaded.BackdropUrl);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
