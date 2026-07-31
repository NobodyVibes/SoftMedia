using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

/// Todo 08 — library-path canonicalisation. Verifies that admin-supplied paths
/// are normalised (trailing separators stripped, relative segments resolved)
/// at insert / update time so two equivalent inputs produce the same stored
/// value and so StreamSecurityService and duplicate-detection both compare
/// apples to apples.
public class LibraryServicePathCanonicalisationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempSub;

    public LibraryServicePathCanonicalisationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-libpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempSub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(_tempSub);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task CreateLibrary_StripsTrailingSeparator()
    {
        var svc = BuildService(out var repo);
        var withSlash = _tempDir + Path.DirectorySeparatorChar;

        var lib = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "X", Type = LibraryType.Movie, Paths = new List<string> { withSlash }
        });

        Assert.Equal(_tempDir, lib.Paths.Single());
        repo.Verify(r => r.AddAsync(It.Is<Library>(l =>
            l.Paths.Count == 1 && l.Paths[0] == _tempDir)), Times.Once);
    }

    [Fact]
    public async Task CreateLibrary_ResolvesRelativeSegments()
    {
        var svc = BuildService(out var repo);
        var viaDotDot = Path.Combine(_tempSub, "..");

        var lib = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Y", Type = LibraryType.Movie, Paths = new List<string> { viaDotDot }
        });

        Assert.Equal(_tempDir, lib.Paths.Single());
        repo.Verify(r => r.AddAsync(It.IsAny<Library>()), Times.Once);
    }

    [Fact]
    public async Task CreateLibrary_DuplicatesInSameRequest_AreRejected()
    {
        var svc = BuildService(out _);
        var withSlash = _tempDir + Path.DirectorySeparatorChar;
        var viaDotDot = Path.Combine(_tempSub, "..");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "Z", Type = LibraryType.Movie,
                Paths = new List<string> { withSlash, viaDotDot }
            }));
    }

    [Fact]
    public async Task CreateLibrary_NonexistentPath_IsRejected()
    {
        var svc = BuildService(out _);
        var ghost = Path.Combine(_tempDir, "does-not-exist");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "G", Type = LibraryType.Movie, Paths = new List<string> { ghost }
            }));
    }

    [Fact]
    public async Task CreateLibrary_EmptyPath_IsRejected()
    {
        var svc = BuildService(out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "E", Type = LibraryType.Movie, Paths = new List<string> { "   " }
            }));
    }

    [Fact]
    public async Task UpdateLibrary_WithTrailingSeparator_StoresCanonicalForm()
    {
        // Seed an existing library that the mock repo will return for Update.
        var existing = new Library
        {
            Id = Guid.NewGuid(), Name = "Old", Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir },
        };

        var libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { existing });
        libraryRepo.Setup(r => r.UpdateAsync(It.IsAny<Library>())).Returns(Task.CompletedTask);

        var svc = BuildServiceWithRepo(libraryRepo.Object);
        var viaDotDot = Path.Combine(_tempSub, "..");

        await svc.UpdateLibraryAsync(existing.Id, new UpdateLibraryRequest
        {
            Name = "Renamed", Type = LibraryType.Movie,
            Paths = new List<string> { viaDotDot + Path.DirectorySeparatorChar },
        });

        // The entity the service mutates in place should hold the canonical form.
        Assert.Equal(_tempDir, existing.Paths.Single());
        libraryRepo.Verify(r => r.UpdateAsync(It.Is<Library>(l => l.Paths[0] == _tempDir)), Times.Once);
    }

    [Fact]
    public async Task UpdateLibrary_PathCollidesWithAnotherLibraryCanonicalForm_IsRejected()
    {
        var other = new Library
        {
            Id = Guid.NewGuid(), Name = "Other", Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir }, // raw-form storage
        };
        var me = new Library
        {
            Id = Guid.NewGuid(), Name = "Me", Type = LibraryType.Movie,
            Paths = new List<string> { _tempSub },
        };

        var libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetByIdAsync(me.Id)).ReturnsAsync(me);
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { other, me });

        // The collision check reads the CONTEXT (unfiltered by design), not the
        // repository — seed both so the test exercises the real read path.
        var svc = BuildServiceWithRepo(libraryRepo.Object, out var db);
        db.Libraries.AddRange(other, me);
        db.SaveChanges();

        // Try to update `me` so that its path collides with `other` via a
        // traversal alias. Expect rejection.
        var alias = Path.Combine(_tempSub, "..");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateLibraryAsync(me.Id, new UpdateLibraryRequest
            {
                Name = "Me", Type = LibraryType.Movie,
                Paths = new List<string> { alias },
            }));
    }

    private LibraryService BuildServiceWithRepo(ILibraryRepository libraryRepo)
        => BuildServiceWithRepo(libraryRepo, out _);

    private LibraryService BuildServiceWithRepo(ILibraryRepository libraryRepo, out AppDbContext dbOut)
    {
        var mediaRepo = new Mock<IMediaRepository>();
        var scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var watcher = new LibraryWatcher(null!, NullLogger<LibraryWatcher>.Instance);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-tests-{Guid.NewGuid()}")
            .Options);
        dbOut = db;

        var (access, ratings) = UnrestrictedProviders();
        return new LibraryService(
            libraryRepo, mediaRepo.Object, scanQueue.Object, imageCache.Object,
            watcher, db, access, ratings,
            new Mock<ILibraryCleanupService>().Object, NullLogger<LibraryService>.Instance);
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

        // LibraryWatcher is a concrete class — supplying a trivial instance is
        // fine here because the helpers we test do not touch it.
        var watcher = new LibraryWatcher(
            scopeFactory: null!,
            logger: NullLogger<LibraryWatcher>.Instance);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-tests-{Guid.NewGuid()}")
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
