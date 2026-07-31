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

/// <summary>
/// Path-uniqueness across create/update, pinned after a real incident: an admin
/// deleted a library, and re-adding its folder was rejected with "already used by
/// another library" — the owner being a second library that had been allowed to claim
/// the same folder because CREATE compared paths with a raw case-sensitive Contains
/// while UPDATE canonicalised. These tests pin the unified check.
/// </summary>
public class LibraryServicePathCollisionTests : IDisposable
{
    private readonly string _tempDir;

    public LibraryServicePathCollisionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-pathcol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// The corruption vector from the incident: a stored path differing only in FORM
    /// (here, casing — Windows treats it as the same folder) must still collide.
    /// The old raw Contains let this create through, producing two libraries that
    /// double-scanned one folder.
    /// </summary>
    [Fact]
    public async Task Create_CollidesWithStoredPathDifferingOnlyInCasing()
    {
        // Skip where the filesystem itself is case-sensitive — flipping the casing
        // there genuinely names a different directory and SHOULD be allowed.
        if (!OperatingSystem.IsWindows()) return;

        var (svc, db) = BuildService();
        SeedLibrary(db, "Tests", _tempDir.ToUpperInvariant());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "Movies",
                Type = LibraryType.Movie,
                Paths = new List<string> { _tempDir.ToLowerInvariant() },
            }));

        Assert.Contains("already used", ex.Message);
    }

    /// <summary>
    /// The error must NAME the owning library. "another library" left the admin
    /// hunting through every library's paths to find which one held the folder.
    /// </summary>
    [Fact]
    public async Task Create_CollisionNamesTheOwningLibrary()
    {
        var (svc, db) = BuildService();
        SeedLibrary(db, "Tests", _tempDir);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "Movies",
                Type = LibraryType.Movie,
                Paths = new List<string> { _tempDir },
            }));

        Assert.Contains("'Tests'", ex.Message);
    }

    /// <summary>
    /// Ported from the removed IsPathUsedAsync_DoesNotApplyAclFilter: the collision
    /// check is an integrity check and must see ALL libraries, including ones the
    /// caller's ACL hides — otherwise a restricted caller could create a library
    /// silently sharing a folder with one they cannot see.
    /// </summary>
    [Fact]
    public async Task Create_CollisionFiresEvenWhenAclHidesTheOwningLibrary()
    {
        var owner = Guid.NewGuid();
        var (svc, db) = BuildService(access: LibraryAccess.AllowOnly(new[] { Guid.NewGuid() }));
        SeedLibrary(db, "Hidden", _tempDir, id: owner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateLibraryAsync(new CreateLibraryRequest
            {
                Name = "Movies",
                Type = LibraryType.Movie,
                Paths = new List<string> { _tempDir },
            }));
    }

    /// <summary>
    /// A legacy row holding a path Canonicalise cannot parse must not brick every
    /// create on the server — it falls back to raw comparison for that row.
    /// </summary>
    [Fact]
    public async Task Create_SurvivesALegacyRowWithAnUnparseablePath()
    {
        var (svc, db) = BuildService();
        SeedLibrary(db, "Broken", "   "); // Canonicalise throws on whitespace

        var lib = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir },
        });

        Assert.Equal("Movies", lib.Name);
    }

    /// <summary>
    /// The user's core loop: delete a library, add the same folder back. The delete
    /// frees the path; the re-create must succeed and the delete must report true.
    /// </summary>
    [Fact]
    public async Task DeleteThenRecreateAtTheSamePath_Succeeds()
    {
        var libId = Guid.NewGuid();
        var (svc, db) = BuildService(configureRepo: (repo, ctx) =>
        {
            repo.Setup(r => r.GetByIdAsync(libId))
                .ReturnsAsync(() => ctx.Libraries.AsNoTracking().FirstOrDefault(l => l.Id == libId));
            repo.Setup(r => r.DeleteAsync(It.IsAny<Library>()))
                .Returns<Library>(async l =>
                {
                    var tracked = await ctx.Libraries.FindAsync(l.Id);
                    if (tracked != null) { ctx.Libraries.Remove(tracked); await ctx.SaveChangesAsync(); }
                });
        });
        SeedLibrary(db, "Movies", _tempDir, id: libId);

        Assert.True(await svc.DeleteLibraryAsync(libId));

        var recreated = await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { _tempDir },
        });
        Assert.Equal("Movies", recreated.Name);
    }

    /// <summary>
    /// Deleting an id that resolves to nothing must say so (the controller turns this
    /// into 404) — a silent success told the admin a stale row was gone when nothing
    /// happened.
    /// </summary>
    [Fact]
    public async Task Delete_ReturnsFalseWhenNothingWasDeleted()
    {
        var (svc, _) = BuildService();
        Assert.False(await svc.DeleteLibraryAsync(Guid.NewGuid()));
    }

    // --- harness ------------------------------------------------------------

    private static void SeedLibrary(AppDbContext db, string name, string path, Guid? id = null)
    {
        db.Libraries.Add(new Library
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Type = LibraryType.Movie,
            Paths = new List<string> { path },
        });
        db.SaveChanges();
    }

    private (LibraryService svc, AppDbContext db) BuildService(
        LibraryAccess? access = null,
        Action<Mock<ILibraryRepository>, AppDbContext>? configureRepo = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-pathcol-{Guid.NewGuid()}")
            .Options);

        var libraryRepo = new Mock<ILibraryRepository>();
        libraryRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<Library>());
        libraryRepo.Setup(r => r.AddAsync(It.IsAny<Library>()))
            .Returns<Library>(async l => { db.Libraries.Add(l); await db.SaveChangesAsync(); });
        configureRepo?.Invoke(libraryRepo, db);

        var mediaRepo = new Mock<IMediaRepository>();
        mediaRepo.Setup(r => r.GetMediaIdsAndTypesByLibraryAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<(Guid, MediaType)>());
        var scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var watcher = new LibraryWatcher(null!, NullLogger<LibraryWatcher>.Instance);

        var accessProvider = new Mock<IUserLibraryAccessProvider>();
        accessProvider.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access ?? LibraryAccess.Unrestricted);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(p => p.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);

        var svc = new LibraryService(
            libraryRepo.Object, mediaRepo.Object, scanQueue.Object, imageCache.Object,
            watcher, db, accessProvider.Object, ratings.Object,
            new Mock<ILibraryCleanupService>().Object,
            NullLogger<LibraryService>.Instance);
        return (svc, db);
    }
}
