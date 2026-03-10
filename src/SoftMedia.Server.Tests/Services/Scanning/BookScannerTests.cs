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

public class TestableBookScanner : BookScanner
{
    public TestableBookScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<BookScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue) 
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue)
    {
    }

    public new Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        return base.ProcessFileAsync(context, filePath, existing, library, cancellationToken);
    }
}

public class BookScannerTests : IDisposable
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILogger<BookScanner>> _mockLogger;
    private readonly Mock<IMediaNotificationService> _mockNotification;
    private readonly Mock<IMetadataQueue> _mockQueue;
    private readonly Mock<IMediaAnalysisService> _mockMediaAnalysis;
    private readonly AppDbContext _dbContext;

    public BookScannerTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockLogger = new Mock<ILogger<BookScanner>>();
        _mockNotification = new Mock<IMediaNotificationService>();
        _mockQueue = new Mock<IMetadataQueue>();
        _mockMediaAnalysis = new Mock<IMediaAnalysisService>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
    }

    [Fact]
    public async Task ProcessFileAsync_ShouldCreateNewBook_WhenNotExists()
    {
        // Arrange
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);
            
        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + " - Frank Herbert - Dune.epub";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);
        
        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };

            // Act
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, null, library, CancellationToken.None);

            await _dbContext.SaveChangesAsync();

            // Assert
            Assert.Equal(ScanResult.New, result.Result);

            var book = await _dbContext.MediaItems.FirstOrDefaultAsync();
            Assert.NotNull(book);
            Assert.Equal(MediaType.Book, book.Type);
            Assert.Equal(fileInfo.FullName, book.Path);
            Assert.Equal(library.Id, book.LibraryId);
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_ShouldSkip_WhenBookAlreadyExists()
    {
        // Arrange
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);
            
        var tempFile = Path.GetTempFileName();
        var destFile = tempFile + " - Frank Herbert - Dune.epub";
        File.Move(tempFile, destFile);
        var fileInfo = new FileInfo(destFile);
        
        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };

            var existingResource = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = fileInfo.FullName,
                Title = "Dune",
                Type = MediaType.Book,
                MetadataJson = "{\"poster\": \"http://example.com/poster.jpg\"}" // Needs actual metadata to trigger Skip
            };
            
            _dbContext.MediaItems.Add(existingResource);
            await _dbContext.SaveChangesAsync();

            // Act
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existingResource, library, CancellationToken.None);

            // Assert
            Assert.Equal(ScanResult.Skipped, result.Result);
            var count = await _dbContext.MediaItems.CountAsync();
            Assert.Equal(1, count); // No new duplicates
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
