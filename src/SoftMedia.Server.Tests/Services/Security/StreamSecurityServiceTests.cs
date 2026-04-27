using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security;
using System.Runtime.InteropServices;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// Todo 09 — dedicated regression tests for the path-jail check that every
/// file-serving endpoint relies on. Covers canonicalisation, the trailing-
/// separator "sibling directory" bug that the service is specifically written
/// to avoid, malformed input handling, and the ValidateMediaAccess state
/// machine (file missing vs unauthorised vs allowed).
public class StreamSecurityServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _libRoot;
    private readonly string _siblingRoot;
    private readonly string _fileInside;
    private readonly string _fileInSibling;

    public StreamSecurityServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "streamsec-tests-" + Guid.NewGuid().ToString("N"));
        _libRoot = Path.Combine(_tempRoot, "Movies");
        _siblingRoot = Path.Combine(_tempRoot, "Movies-secret");
        Directory.CreateDirectory(_libRoot);
        Directory.CreateDirectory(_siblingRoot);
        _fileInside = Path.Combine(_libRoot, "file.mkv");
        _fileInSibling = Path.Combine(_siblingRoot, "leak.mkv");
        File.WriteAllText(_fileInside, "inside");
        File.WriteAllText(_fileInSibling, "sibling");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private StreamSecurityService NewService() =>
        new(NullLogger<StreamSecurityService>.Instance);

    // --- IsPathAuthorized ---------------------------------------------------

    [Fact]
    public void IsPathAuthorized_PathInsideLibrary_ReturnsTrue()
    {
        Assert.True(NewService().IsPathAuthorized(_fileInside, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_PathOutsideLibrary_ReturnsFalse()
    {
        var outside = Path.Combine(_tempRoot, "random.txt");
        File.WriteAllText(outside, "nope");
        Assert.False(NewService().IsPathAuthorized(outside, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_SiblingDirectoryWithMatchingPrefix_ReturnsFalse()
    {
        // Regression guard for the classic "/libs/movies" vs "/libs/movies-secret"
        // partial-match bug. StreamSecurityService appends a DirectorySeparator
        // before comparing — this test fails if that logic regresses.
        Assert.False(NewService().IsPathAuthorized(_fileInSibling, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_PathWithTraversal_CanonicalisesThenDenies()
    {
        var traversal = Path.Combine(_libRoot, "..", "Movies-secret", "leak.mkv");
        Assert.False(NewService().IsPathAuthorized(traversal, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_PathWithTraversalIntoLibrary_Succeeds()
    {
        // Traversal that happens to land back inside the jail is legitimate —
        // the canonical form is identical to a path already inside the jail.
        var sneaky = Path.Combine(_libRoot, "..", "Movies", "file.mkv");
        Assert.True(NewService().IsPathAuthorized(sneaky, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_EmptyLibraryPaths_ReturnsFalse()
    {
        Assert.False(NewService().IsPathAuthorized(_fileInside, Array.Empty<string>()));
    }

    [Fact]
    public void IsPathAuthorized_NullOrWhitespaceFilePath_ReturnsFalse()
    {
        var svc = NewService();
        Assert.False(svc.IsPathAuthorized(null!, new[] { _libRoot }));
        Assert.False(svc.IsPathAuthorized("", new[] { _libRoot }));
        Assert.False(svc.IsPathAuthorized("   ", new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_MalformedFilePath_ReturnsFalse_WithoutThrowing()
    {
        // Invalid characters cause Path.GetFullPath to throw — the service
        // must swallow the exception and return false rather than 500ing.
        var malformed = "\0invalid\0path";
        Assert.False(NewService().IsPathAuthorized(malformed, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_CaseSensitivityFollowsHostOS()
    {
        // StreamSecurityService uses StringComparison.OrdinalIgnoreCase, so on
        // any OS the upper-cased variant matches. We assert the contract the
        // service is written to — consistent behaviour across platforms.
        var upperInside = _fileInside.ToUpperInvariant();
        var upperRoot = _libRoot.ToUpperInvariant();

        // On Windows the filesystem is case-insensitive anyway, so the canonical
        // forms of upper/lower variants are identical. On Linux the upper-cased
        // path won't exist, but IsPathAuthorized doesn't check existence — it
        // only normalises and compares.
        Assert.True(NewService().IsPathAuthorized(upperInside, new[] { upperRoot }));
    }

    // --- ValidateMediaAccess -----------------------------------------------

    [Fact]
    public void ValidateMediaAccess_NullItem_ReturnsFileNotFound()
    {
        Assert.Equal(MediaAccessResult.FileNotFound, NewService().ValidateMediaAccess(null!));
    }

    [Fact]
    public void ValidateMediaAccess_ItemWithoutLibrary_ReturnsFileNotFound()
    {
        var item = new MediaItem { Id = Guid.NewGuid(), Path = _fileInside, Title = "x", SortTitle = "x" };
        Assert.Equal(MediaAccessResult.FileNotFound, NewService().ValidateMediaAccess(item));
    }

    [Fact]
    public void ValidateMediaAccess_FileMissingOnDisk_ReturnsFileNotFound()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "X", Type = LibraryType.Movie, Paths = new List<string> { _libRoot } };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Library = library, Title = "x", SortTitle = "x",
            Path = Path.Combine(_libRoot, "ghost.mkv")
        };
        Assert.Equal(MediaAccessResult.FileNotFound, NewService().ValidateMediaAccess(item));
    }

    [Fact]
    public void ValidateMediaAccess_FilePresentButOutsideLibrary_ReturnsUnauthorized()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "X", Type = LibraryType.Movie, Paths = new List<string> { _libRoot } };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Library = library, Title = "x", SortTitle = "x",
            Path = _fileInSibling
        };
        Assert.Equal(MediaAccessResult.Unauthorized, NewService().ValidateMediaAccess(item));
    }

    [Fact]
    public void ValidateMediaAccess_AllGood_ReturnsAllowed()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "X", Type = LibraryType.Movie, Paths = new List<string> { _libRoot } };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), Library = library, Title = "x", SortTitle = "x",
            Path = _fileInside
        };
        Assert.Equal(MediaAccessResult.Allowed, NewService().ValidateMediaAccess(item));
    }

    [Fact]
    public void ValidateMediaAccess_EmptyPath_ReturnsFileNotFound()
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "X", Type = LibraryType.Movie, Paths = new List<string> { _libRoot } };
        var item = new MediaItem { Id = Guid.NewGuid(), Library = library, Title = "x", SortTitle = "x", Path = "" };
        Assert.Equal(MediaAccessResult.FileNotFound, NewService().ValidateMediaAccess(item));
    }

    // --- Symlink resolution (SDD §6.2 LFI guard) ---------------------------
    //
    // Path.GetFullPath alone collapses `..` but does not follow symlinks. On
    // Linux an admin who unknowingly adds a library root containing a symlink
    // would otherwise re-introduce LFI. These tests exercise the
    // ResolveLinkTarget(returnFinalTarget:true) path inside StreamSecurityService.
    //
    // Symlink creation requires elevated privileges on Windows (or Developer
    // Mode), so when the OS rejects creation we silently return — the security
    // claim being asserted is specifically the Linux risk model.

    private static bool TryCreateSymlink(string link, string target)
    {
        try
        {
            if (Directory.Exists(target))
                Directory.CreateSymbolicLink(link, target);
            else
                File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public void IsPathAuthorized_SymlinkInsideJailEscapingTarget_ReturnsFalse()
    {
        // Set-up: /tmp/.../Movies/escape -> /tmp/.../Movies-secret
        // The literal path "/tmp/.../Movies/escape/leak.mkv" *does* start with the
        // canonical library prefix, so a naive prefix check would allow it. With
        // symlink resolution the real path is /tmp/.../Movies-secret/leak.mkv,
        // which does not start with the prefix, so access is denied.
        var symlink = Path.Combine(_libRoot, "escape");
        if (!TryCreateSymlink(symlink, _siblingRoot)) return;

        var probe = Path.Combine(symlink, "leak.mkv");
        Assert.True(File.Exists(probe), "symlink target should resolve to the seeded sibling file");

        Assert.False(NewService().IsPathAuthorized(probe, new[] { _libRoot }));
    }

    [Fact]
    public void IsPathAuthorized_LibraryRootIsSymlink_StillAllowsFilesInsideRealTarget()
    {
        // Set-up: /tmp/.../link-root -> /tmp/.../Movies. An admin who declared
        // a symlinked library root must still be able to serve real files
        // beneath it — the resolver applies to the root as well as the file.
        var rootLink = Path.Combine(_tempRoot, "link-root");
        if (!TryCreateSymlink(rootLink, _libRoot)) return;

        var probe = Path.Combine(rootLink, "file.mkv");
        Assert.True(File.Exists(probe));

        Assert.True(NewService().IsPathAuthorized(probe, new[] { rootLink }));
    }

    [Fact]
    public void IsPathAuthorized_SymlinkPointingBackInsideJail_IsAllowed()
    {
        // Symlink target lands back in the jail — the real path is legitimate.
        // Sanity check that the symlink resolution does not over-block.
        var inwardLink = Path.Combine(_libRoot, "shortcut");
        if (!TryCreateSymlink(inwardLink, _fileInside)) return;

        Assert.True(NewService().IsPathAuthorized(inwardLink, new[] { _libRoot }));
    }
}
