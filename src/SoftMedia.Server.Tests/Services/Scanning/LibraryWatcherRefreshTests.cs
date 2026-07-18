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

namespace SoftMedia.Server.Tests.Services.Scanning;

/// R-WI-007 — libraries created or path-edited while the server is running must
/// (re)register file watchers instead of waiting for the next restart. Verifies that
/// LibraryService pokes the watcher on create/update, that RefreshWatchersAsync no-ops
/// safely when the loop was never started (boot-disabled server / unit tests), and the
/// path-containment helper used to prune stale pending files on a path edit.
public class LibraryWatcherRefreshTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "softmedia-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task CreateLibrary_RefreshesWatchers()
    {
        var watcher = NewMockWatcher();
        var repo = new Mock<ILibraryRepository>();
        repo.Setup(r => r.IsPathUsedAsync(It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<Library>());
        repo.Setup(r => r.AddAsync(It.IsAny<Library>())).Returns(Task.CompletedTask);

        var svc = BuildService(watcher, repo);

        await svc.CreateLibraryAsync(new CreateLibraryRequest
        {
            Name = "Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { NewTempDir() }
        });

        watcher.Verify(w => w.RefreshWatchersAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateLibrary_RefreshesWatchers()
    {
        var watcher = NewMockWatcher();
        var existing = new Library
        {
            Id = Guid.NewGuid(),
            Name = "M",
            Type = LibraryType.Movie,
            Paths = new List<string> { NewTempDir() }
        };
        var repo = new Mock<ILibraryRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { existing });
        repo.Setup(r => r.UpdateAsync(It.IsAny<Library>())).Returns(Task.CompletedTask);

        var svc = BuildService(watcher, repo);

        await svc.UpdateLibraryAsync(existing.Id, new UpdateLibraryRequest
        {
            Name = "M",
            Type = LibraryType.Movie,
            Paths = new List<string> { NewTempDir() }
        });

        watcher.Verify(w => w.RefreshWatchersAsync(), Times.Once);
    }

    [Fact]
    public async Task RefreshWatchers_NoOps_WhenLoopNotRunning()
    {
        // Built with a null scope factory and never started — the boot-disabled server
        // and every unit-test case. The _isRunning guard must make this a safe no-op
        // rather than dereferencing the null scope factory.
        var watcher = new LibraryWatcher(null!, NullLogger<LibraryWatcher>.Instance);

        await watcher.RefreshWatchersAsync(); // must not throw
    }

    [Fact]
    public void IsPathUnderRoot_Containment()
    {
        var root = NewTempDir();

        Assert.True(LibraryWatcher.IsPathUnderRoot(Path.Combine(root, "sub", "file.mkv"), root));
        Assert.True(LibraryWatcher.IsPathUnderRoot(root, root)); // the path IS the root
        Assert.False(LibraryWatcher.IsPathUnderRoot(root + "2", root)); // prefix sibling, not inside
        Assert.False(LibraryWatcher.IsPathUnderRoot(NewTempDir(), root)); // unrelated dir
    }

    private static Mock<LibraryWatcher> NewMockWatcher()
    {
        var watcher = new Mock<LibraryWatcher>(null!, NullLogger<LibraryWatcher>.Instance) { CallBase = false };
        watcher.Setup(w => w.RefreshWatchersAsync()).Returns(Task.CompletedTask);
        return watcher;
    }

    private static LibraryService BuildService(Mock<LibraryWatcher> watcher, Mock<ILibraryRepository> repo)
    {
        var mediaRepo = new Mock<IMediaRepository>();
        var scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"watcher-refresh-{Guid.NewGuid()}")
            .Options);

        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(p => p.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);

        return new LibraryService(
            repo.Object, mediaRepo.Object, scanQueue.Object, imageCache.Object,
            watcher.Object, db, access.Object, ratings.Object,
            NullLogger<LibraryService>.Instance);
    }
}
