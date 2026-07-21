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

/// Wave A — verifies that LibraryType.Photo is rejected at the service boundary
/// while the project ships without a PhotoScanner. The enum value and the
/// EXIF metadata provider remain in place; only Create / Update guard against
/// the type so the admin UI cannot accidentally produce empty libraries.
public class LibraryServiceCreatePhotoTests : IDisposable
{
    private readonly string _tempDir;

    public LibraryServiceCreatePhotoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-photo-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task CreateLibrary_PhotoType_ThrowsArgumentExceptionMentioningPhase2()
    {
        var svc = BuildService(out _);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "Vacation Photos",
                Type = LibraryType.Photo,
                Paths = new List<string> { _tempDir }
            }));

        Assert.Contains("Phase 2", ex.Message);
    }

    [Fact]
    public async Task UpdateLibrary_PhotoType_ThrowsArgumentExceptionMentioningPhase2()
    {
        // Seed a Movie library; admin attempts to flip its type to Photo.
        var existing = new Library
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir },
        };

        var libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { existing });

        var svc = BuildServiceWithRepo(libraryRepo.Object);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateLibraryAsync(existing.Id, new UpdateLibraryRequest
            {
                Name = "Movies",
                Type = LibraryType.Photo,
                Paths = new List<string> { _tempDir }
            }));

        Assert.Contains("Phase 2", ex.Message);

        // The repo's UpdateAsync must NOT have been called — the guard fires
        // before any mutation reaches the storage layer.
        libraryRepo.Verify(r => r.UpdateAsync(It.IsAny<Library>()), Times.Never);
    }

    [Fact]
    public async Task CreateLibrary_NonPhotoType_StillSucceeds()
    {
        // Regression guard: the new check must only short-circuit on Photo.
        var svc = BuildService(out var repo);

        var lib = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir }
        });

        Assert.Equal(LibraryType.Movie, lib.Type);
        repo.Verify(r => r.AddAsync(It.IsAny<Library>()), Times.Once);
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
            watcher, db, access, ratings, NullLogger<LibraryService>.Instance);
    }

    // audit wave-2 WS-2: LibraryService now takes the ACL + rating providers; tests run unrestricted.
    private static (IUserLibraryAccessProvider, IUserContentRatingProvider) UnrestrictedProviders()
    {
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(p => p.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        return (access.Object, ratings.Object);
    }

    private LibraryService BuildService(out Mock<ILibraryRepository> libraryRepo)
    {
        libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<Library>());
        libraryRepo.Setup(r => r.AddAsync(It.IsAny<Library>())).Returns(Task.CompletedTask);

        var mediaRepo = new Mock<IMediaRepository>();
        var scanQueue = new Mock<ILibraryScanQueueService>();
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
            NullLogger<LibraryService>.Instance);
    }
}
