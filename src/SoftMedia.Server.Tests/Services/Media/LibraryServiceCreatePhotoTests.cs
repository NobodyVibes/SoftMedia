using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Photo libraries were guarded off while no PhotoScanner existed (Wave A). The
/// scanner has landed, the guard is gone — these tests now pin the ENABLED behaviour:
/// Photo creates/updates flow through the same path as every other library type.
public class LibraryServiceCreatePhotoTests : IDisposable
{
    private readonly string _tempDir;

    public LibraryServiceCreatePhotoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-photo-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task CreateLibrary_PhotoType_Succeeds_AndEnqueuesScan()
    {
        var svc = BuildService(out var repo, out var scanQueue);

        var lib = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Vacation Photos",
            Type = LibraryType.Photo,
            Paths = new List<string> { _tempDir }
        });

        Assert.Equal(LibraryType.Photo, lib.Type);
        repo.Verify(r => r.AddAsync(It.Is<Library>(l => l.Type == LibraryType.Photo)), Times.Once);
        scanQueue.Verify(q => q.EnqueueScan(lib.Id, lib.Name), Times.Once);
    }

    [Fact]
    public async Task UpdateLibrary_ToPhotoType_Succeeds()
    {
        var existing = new Library
        {
            Id = Guid.NewGuid(),
            Name = "Misc",
            Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir },
        };

        var libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { existing });

        var svc = BuildServiceWithRepo(libraryRepo.Object);

        await svc.UpdateLibraryAsync(existing.Id, new UpdateLibraryRequest
        {
            Name = "Photos",
            Type = LibraryType.Photo,
            Paths = new List<string> { _tempDir }
        });

        libraryRepo.Verify(r => r.UpdateAsync(It.Is<Library>(l => l.Type == LibraryType.Photo)), Times.Once);
    }

    private LibraryService BuildServiceWithRepo(ILibraryRepository libraryRepo)
    {
        var mediaRepo = new Mock<IMediaRepository>();
        var scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var watcher = new LibraryWatcher(null!, NullLogger<LibraryWatcher>.Instance);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-photo-{Guid.NewGuid()}")
            .Options);

        var (access, ratings) = UnrestrictedProviders();
        return new LibraryService(
            libraryRepo, mediaRepo.Object, scanQueue.Object, imageCache.Object,
            watcher, db, access, ratings,
            new Mock<ILibraryCleanupService>().Object, NullLogger<LibraryService>.Instance);
    }

    // audit wave-2 WS-2: LibraryService takes the ACL + rating providers; tests run unrestricted.
    private static (IUserLibraryAccessProvider, IUserContentRatingProvider) UnrestrictedProviders()
    {
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(p => p.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        return (access.Object, ratings.Object);
    }

    private LibraryService BuildService(out Mock<ILibraryRepository> libraryRepo, out Mock<ILibraryScanQueueService> scanQueue)
    {
        libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<Library>());
        libraryRepo.Setup(r => r.AddAsync(It.IsAny<Library>())).Returns(Task.CompletedTask);

        var mediaRepo = new Mock<IMediaRepository>();
        scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var watcher = new LibraryWatcher(
            scopeFactory: null!,
            logger: NullLogger<LibraryWatcher>.Instance);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-photo-{Guid.NewGuid()}")
            .Options);

        var (access, ratings) = UnrestrictedProviders();
        return new LibraryService(
            libraryRepo.Object,
            mediaRepo.Object,
            scanQueue.Object,
            imageCache.Object,
            watcher,
            db,
            access,
            ratings,
            new Mock<ILibraryCleanupService>().Object,
            NullLogger<LibraryService>.Instance);
    }
}
