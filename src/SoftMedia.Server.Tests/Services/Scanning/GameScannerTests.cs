using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class TestableGameScanner : GameScanner
{
    public TestableGameScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<GameScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue) 
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue)
    {
    }

    public bool IsStrictEnrichment
    {
        get => _strictEnrichment;
        set => _strictEnrichment = value;
    }

    public Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var fileDiscover = new FileDiscoveryResult(fileInfo.FullName, fileInfo.Exists ? fileInfo.Length : 0, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow);
        return base.ProcessFileAsync(context, fileDiscover, existing, library, cancellationToken);
    }
}

public class GameScannerTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILogger<GameScanner>> _mockLogger;
    private readonly Mock<IMediaNotificationService> _mockNotification;
    private readonly Mock<IMetadataQueue> _mockQueue;
    private readonly Mock<IMediaAnalysisService> _mockMediaAnalysis;
    private readonly AppDbContext _dbContext;

    public GameScannerTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockLogger = new Mock<ILogger<GameScanner>>();
        _mockNotification = new Mock<IMediaNotificationService>();
        _mockQueue = new Mock<IMetadataQueue>();
        _mockMediaAnalysis = new Mock<IMediaAnalysisService>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
    }

    [Fact]
    public async Task ProcessFileAsync_CreatesNewGame_WhenNotExists()
    {
        // Arrange
        var scanner = new TestableGameScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);
            
        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + ".iso";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);
        
        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Games", Type = LibraryType.Game };

            // Act
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, null, library, CancellationToken.None);

            await _dbContext.SaveChangesAsync();

            // Assert
            Assert.Equal(ScanResult.New, result.Result);

            var game = await _dbContext.MediaItems.FirstOrDefaultAsync();
            Assert.NotNull(game);
            Assert.Equal(MediaType.Game, game.Type);
            Assert.Equal(fileInfo.FullName, game.Path);
            Assert.Equal(library.Id, game.LibraryId);
            Assert.True(result.EnqueueMetadata); 
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_SkipsExistingGame_WhenRelaxedAndHasPoster()
    {
        // Arrange
        var scanner = new TestableGameScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object)
        {
            IsStrictEnrichment = false
        };
            
        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + ".iso";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);
        
        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Games", Type = LibraryType.Game };

            var existingResource = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = fileInfo.FullName,
                Title = "Test Game",
                Type = MediaType.Game,
                PosterUrl = "http://example.com/poster.jpg",
                // SM-WI-055/S10: change detection exists now — an unchanged file must
                // match on Size + DateModified to hit the Skipped path.
                Size = fileInfo.Length,
                DateModified = fileInfo.LastWriteTimeUtc,
            };

            _dbContext.MediaItems.Add(existingResource);
            await _dbContext.SaveChangesAsync();

            // Act
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existingResource, library, CancellationToken.None);

            // Assert
            Assert.Equal(ScanResult.Skipped, result.Result);
            Assert.False(result.EnqueueMetadata);
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_PreservesEnrichedYearAndLockedTitle_OnRescan()
    {
        // SM-WI-010: identity fields are stamped from the filename only for NEW rows.
        var scanner = new TestableGameScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + ".iso";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Games", Type = LibraryType.Game };
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = fileInfo.FullName,
                Title = "Corrected Game Title",
                Year = 1998, // enriched — the temp filename parses to no year
                Type = MediaType.Game,
                MetadataLocked = true,
                PosterUrl = "http://example.com/poster.jpg"
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existing, library, CancellationToken.None);

            Assert.Equal("Corrected Game Title", existing.Title);
            Assert.Equal(1998, existing.Year);
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_DoesNotNullEnrichedYear_ForUnlockedYearlessFilename()
    {
        var scanner = new TestableGameScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + ".iso";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Games", Type = LibraryType.Game };
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = fileInfo.FullName,
                Title = "Some Game",
                Year = 2004, // enriched; filename has no year — must survive the rescan
                Type = MediaType.Game,
                PosterUrl = "http://example.com/poster.jpg"
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existing, library, CancellationToken.None);

            Assert.Equal(2004, existing.Year);
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_ReAnalyzesChangedFile_SkipsUnchanged()
    {
        // SM-WI-055/S10: GameScanner previously had NO change detection — a replaced
        // ROM kept stale analysis until deleted and re-added.
        var scanner = new TestableGameScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + ".iso";
        File.Move(tempFile, destFile);
        File.WriteAllText(destFile, "v1");
        var fileInfo = new FileInfo(destFile);

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Games", Type = LibraryType.Game };
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = fileInfo.FullName,
                Title = "Some Game",
                Type = MediaType.Game,
                PosterUrl = "http://example.com/poster.jpg", // complete → skip path
                Size = fileInfo.Length,
                DateModified = fileInfo.LastWriteTimeUtc,
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            // Unchanged: no analysis, Skipped.
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existing, library, CancellationToken.None);
            Assert.Equal(ScanResult.Skipped, result.Result);
            _mockMediaAnalysis.Verify(a => a.AnalyzeAsync(
                It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<MetadataRefreshMode>(), It.IsAny<CancellationToken>()), Times.Never);

            // Replace the file (size changes): full re-analysis, Updated.
            File.WriteAllText(destFile, "version-two-larger");
            result = await scanner.ProcessFileAsync(_dbContext, destFile, existing, library, CancellationToken.None);
            Assert.Equal(ScanResult.Updated, result.Result);
            _mockMediaAnalysis.Verify(a => a.AnalyzeAsync(
                It.IsAny<MediaItem>(), It.IsAny<string>(), MetadataRefreshMode.Full, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
