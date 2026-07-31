using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// SM-WI-060 — a directory moved into the watched tree (the standard *arr / torrent
/// completed-folder move) raises ONE Created event for the folder and none for its
/// children. The watcher must pend the contained media files instead of silently
/// ignoring the event (the old code returned at the IsMediaFile check).
/// </summary>
public class LibraryWatcherDirectoryCreatedTests : IDisposable
{
    private sealed class ProbeWatcher : LibraryWatcher
    {
        public ProbeWatcher(IServiceScopeFactory scopeFactory)
            : base(scopeFactory, NullLogger<LibraryWatcher>.Instance)
        {
        }

        public void RaiseCreated(string fullPath, Guid libraryId) => OnFileCreated(fullPath, libraryId);
    }

    private readonly string _tempDir;
    private readonly ProbeWatcher _watcher;

    public LibraryWatcherDirectoryCreatedTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("sm-wi-060-").FullName;
        var scopeFactory = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _watcher = new ProbeWatcher(scopeFactory);
    }

    [Fact]
    public void DirectoryCreated_PendsContainedMediaFiles_IncludingNested()
    {
        // Real per-title layout from the library, moved in as one folder.
        var showDir = Path.Combine(_tempDir, "Disenchantment.S01.COMPLETE.720p.NF.WEBRip.x264-GalaxyTV[TGx]");
        Directory.CreateDirectory(Path.Combine(showDir, "Season 1"));
        File.WriteAllText(Path.Combine(showDir, "Season 1", "Disenchantment.S01E01.mkv"), "x");
        File.WriteAllText(Path.Combine(showDir, "Season 1", "Disenchantment.S01E02.mkv"), "x");
        File.WriteAllText(Path.Combine(showDir, "readme.txt"), "not media");

        _watcher.RaiseCreated(showDir, Guid.NewGuid());

        Assert.Equal(2, _watcher.PendingFileCount); // both episodes pended, txt ignored
    }

    [Fact]
    public void NonMediaSingleFile_IsStillIgnored()
    {
        var file = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(file, "x");

        _watcher.RaiseCreated(file, Guid.NewGuid());

        Assert.Equal(0, _watcher.PendingFileCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
