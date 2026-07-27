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
        IMetadataQueue metadataQueue,
        IBookMetadataExtractor? bookMetadataExtractor = null)
        : base(scopeFactory, logger, notificationService, mediaAnalysisService, metadataQueue,
               bookMetadataExtractor ?? new NullBookMetadataExtractor())
    {
    }

    /// <summary>No-op extractor so existing filename-only tests keep exercising
    /// the <see cref="FileNameParser"/> path without needing real EPUB/PDF bytes.</summary>
    private sealed class NullBookMetadataExtractor : IBookMetadataExtractor
    {
        public Task<BookFileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult<BookFileMetadata?>(null);
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
                PosterUrl = "http://example.com/poster.jpg" // Needs actual metadata to trigger Skip
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

    [Fact]
    public async Task ProcessFileAsync_RequeuesExistingBook_WhenStrictAndMissingAuthor()
    {
        // Arrange
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object)
        {
            IsStrictEnrichment = true
        };
            
        var tempFile = Path.GetTempPath();
        var destFile = Path.Combine(tempFile, "Dune.epub"); // No " - " separator, author will be empty
        if (File.Exists(destFile)) File.Delete(destFile);
        File.WriteAllText(destFile, "dummy content");
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
                PosterUrl = "http://example.com/poster.jpg" // Has poster but no cast/publisher
            };
            
            _dbContext.MediaItems.Add(existingResource);
            await _dbContext.SaveChangesAsync();

            // Act
            var result = await scanner.ProcessFileAsync(_dbContext, fileInfo.FullName, existingResource, library, CancellationToken.None);

            // Assert
            Assert.Equal(ScanResult.Updated, result.Result);
            Assert.True(result.EnqueueMetadata); 
        }
        finally
        {
            if (File.Exists(destFile)) File.Delete(destFile);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_SkipsExistingBook_WhenRelaxedAndHasPoster()
    {
        // Arrange
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object)
        {
            IsStrictEnrichment = false
        };
            
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
                PosterUrl = "http://example.com/poster.jpg" // Has poster but no cast/publisher
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

    // ─────────────────────────────────────────────────────────── Comic pipeline

    [Fact]
    public async Task ProcessFileAsync_Comic_CreatesSeriesAndIssue()
    {
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        var cbzPath = Path.Combine(tmpDir, "Amazing-Man Comics Issue 005.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 }); // Minimal zip header

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };

            var result = await scanner.ProcessFileAsync(_dbContext, cbzPath, null, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            Assert.Equal(ScanResult.New, result.Result);
            Assert.True(result.EnqueueMetadata, "New comic issues must be enqueued for metadata enrichment");

            var series = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Type == MediaType.ComicSeries);
            var issue = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Type == MediaType.ComicIssue);

            Assert.NotNull(series);
            Assert.NotNull(issue);
            Assert.Equal("Amazing Man Comics", series!.Title);
            Assert.Equal(series.Id, issue!.SeriesId);
            Assert.Equal(5, issue.EpisodeNumber);
            Assert.Equal("Issue #5", issue.Title);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_ExistingComicIssue_SkipsEnqueueWhenComplete()
    {
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object)
        {
            IsStrictEnrichment = false
        };

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        var cbzPath = Path.Combine(tmpDir, "Amazing-Man Comics Issue 005.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };
            // Already-enriched issue: MetadataHash is set, indicating a prior enrichment attempt.
            // The comic enrichment policy uses MetadataHash (not poster) as the signal because
            // comic covers live inside the archive, not as external URLs.
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = cbzPath,
                Type = MediaType.ComicIssue,
                Title = "The Beginning",
                EpisodeNumber = 5,
                MetadataHash = "some-prior-hash"
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            var result = await scanner.ProcessFileAsync(_dbContext, cbzPath, existing, library, CancellationToken.None);

            Assert.Equal(ScanResult.Updated, result.Result);
            Assert.False(result.EnqueueMetadata, "Already-enriched issue should not re-enqueue");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_Comic_ReusesExistingSeriesForSecondIssue()
    {
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        var cbzA = Path.Combine(tmpDir, "Mystery Men Comics Issue 012.cbz");
        var cbzB = Path.Combine(tmpDir, "Mystery Men Comics Issue 013.cbz");
        File.WriteAllBytes(cbzA, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        File.WriteAllBytes(cbzB, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };

            await scanner.ProcessFileAsync(_dbContext, cbzA, null, library, CancellationToken.None);
            await scanner.ProcessFileAsync(_dbContext, cbzB, null, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            var seriesCount = await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.ComicSeries);
            var issueCount = await _dbContext.MediaItems.CountAsync(m => m.Type == MediaType.ComicIssue);

            Assert.Equal(1, seriesCount);   // Both issues share one series parent
            Assert.Equal(2, issueCount);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_Comic_OneShotWithoutIssueNumber()
    {
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        var cbzPath = Path.Combine(tmpDir, "Watchmen Special.cbz");
        File.WriteAllBytes(cbzPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };
            await scanner.ProcessFileAsync(_dbContext, cbzPath, null, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            var series = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Type == MediaType.ComicSeries);
            var issue = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Type == MediaType.ComicIssue);

            Assert.NotNull(series);
            Assert.NotNull(issue);
            Assert.Equal("Watchmen Special", series!.Title);
            Assert.Null(issue!.EpisodeNumber);
            // One-shot falls back to the series name as title (not "Issue #null")
            Assert.Equal("Watchmen Special", issue.Title);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_FlatBook_StillWorks()
    {
        // Regression guard: .pdf/.epub still routed through the flat-book pipeline.
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object, _mockMediaAnalysis.Object, _mockQueue.Object);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        var pdfPath = Path.Combine(tmpDir, "Jane Austen - Pride and Prejudice.pdf");
        File.WriteAllBytes(pdfPath, new byte[] { 0x25, 0x50, 0x44, 0x46 });

        try
        {
            var library = new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book };
            await scanner.ProcessFileAsync(_dbContext, pdfPath, null, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            var book = await _dbContext.MediaItems.FirstOrDefaultAsync();
            Assert.NotNull(book);
            Assert.Equal(MediaType.Book, book!.Type);
            Assert.Null(book.SeriesId);
            Assert.Equal("Pride and Prejudice", book.Title);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ──────────────────────────────────────────── embedded identifiers (ISBN / pages)

    /// <summary>
    /// Extractor stub that always reports the same embedded fields and counts how many times
    /// it was asked. The call count is the point of the fast-path tests: reopening every book
    /// on every rescan was the cost the unchanged-file short-circuit exists to avoid.
    /// </summary>
    private sealed class StubBookMetadataExtractor : IBookMetadataExtractor
    {
        private readonly BookFileMetadata? _result;
        public int Calls { get; private set; }

        public StubBookMetadataExtractor(BookFileMetadata? result) => _result = result;

        public Task<BookFileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static BookFileMetadata EmbeddedFor(string? isbn, int? pageCount) => new(
        Title: "Dune", Author: "Frank Herbert", Year: 1965, Publisher: "Chilton",
        Description: null, Isbn: isbn, Language: "en", PageCount: pageCount);

    private (Library Library, FileInfo File, string Path) NewBookFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"softmedia-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, "dummy content");
        return (new Library { Id = Guid.NewGuid(), Name = "Books", Type = LibraryType.Book },
                new FileInfo(path), path);
    }

    [Fact]
    public async Task ProcessFileAsync_PersistsEmbeddedIsbnAndPageCount_ForNewBook()
    {
        var extractor = new StubBookMetadataExtractor(EmbeddedFor("9780441013593", 412));
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
            _mockMediaAnalysis.Object, _mockQueue.Object, extractor);

        var (library, _, path) = NewBookFile("Dune.epub");
        try
        {
            await scanner.ProcessFileAsync(_dbContext, path, null, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            var book = await _dbContext.MediaItems.FirstAsync();
            Assert.Equal("9780441013593", book.Isbn);
            Assert.Equal(412, book.PageCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_BackfillsIdentifiers_OnUnchangedFileScannedBeforeTheColumnsExisted()
    {
        // Nothing else reopens an unchanged file, so without this a library indexed before
        // Isbn/PageCount were promoted columns would show a blank ISBN and page count forever.
        var extractor = new StubBookMetadataExtractor(EmbeddedFor("9780441013593", 412));
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
            _mockMediaAnalysis.Object, _mockQueue.Object, extractor);

        var (library, info, path) = NewBookFile("Dune.epub");
        try
        {
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = info.FullName,
                Title = "Dune",
                Type = MediaType.Book,
                Size = info.Length,
                DateModified = info.LastWriteTimeUtc,
                PosterUrl = "http://example.com/poster.jpg",
                MetadataHash = "already-enriched",
                Isbn = null,
                PageCount = null
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            var result = await scanner.ProcessFileAsync(
                _dbContext, info.FullName, existing, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            Assert.Equal(1, extractor.Calls);
            // Reported as updated, not skipped, so the row actually gets written back.
            Assert.Equal(ScanResult.Updated, result.Result);
            Assert.Equal("9780441013593", existing.Isbn);
            Assert.Equal(412, existing.PageCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_DoesNotReopenUnchangedFile_OnceIdentifiersArePresent()
    {
        // The backfill guard has to be self-limiting, or every rescan pays to reopen the
        // whole library — the exact cost the unchanged-file fast path was added to remove.
        var extractor = new StubBookMetadataExtractor(EmbeddedFor("9780441013593", 412));
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
            _mockMediaAnalysis.Object, _mockQueue.Object, extractor);

        var (library, info, path) = NewBookFile("Dune.epub");
        try
        {
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = info.FullName,
                Title = "Dune",
                Type = MediaType.Book,
                Size = info.Length,
                DateModified = info.LastWriteTimeUtc,
                PosterUrl = "http://example.com/poster.jpg",
                MetadataHash = "already-enriched",
                PageCount = 412
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            var result = await scanner.ProcessFileAsync(
                _dbContext, info.FullName, existing, library, CancellationToken.None);

            Assert.Equal(0, extractor.Calls);
            Assert.Equal(ScanResult.Skipped, result.Result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessFileAsync_DoesNotOverwriteExistingIdentifiers()
    {
        // A rescan of a changed file must not clobber a value the metadata provider supplied
        // for a field the file itself doesn't declare.
        var extractor = new StubBookMetadataExtractor(EmbeddedFor("9780441013593", 412));
        var scanner = new TestableBookScanner(
            _mockScopeFactory.Object, _mockLogger.Object, _mockNotification.Object,
            _mockMediaAnalysis.Object, _mockQueue.Object, extractor);

        var (library, info, path) = NewBookFile("Dune.epub");
        try
        {
            var existing = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Path = info.FullName,
                Title = "Dune",
                Type = MediaType.Book,
                Size = 1,                       // differs => full re-process path
                DateModified = DateTime.UtcNow.AddDays(-1),
                PosterUrl = "http://example.com/poster.jpg",
                Isbn = "0441013597",
                PageCount = 896
            };
            _dbContext.MediaItems.Add(existing);
            await _dbContext.SaveChangesAsync();

            await scanner.ProcessFileAsync(_dbContext, info.FullName, existing, library, CancellationToken.None);
            await _dbContext.SaveChangesAsync();

            Assert.Equal("0441013597", existing.Isbn);
            Assert.Equal(896, existing.PageCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
