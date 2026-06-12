using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.LibraryAccess;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security;

public class StreamSecurityService : IStreamSecurityService
{
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly ILogger<StreamSecurityService> _logger;

    public StreamSecurityService(
        IUserLibraryAccessProvider libraryAccessProvider,
        ILogger<StreamSecurityService> logger)
    {
        _libraryAccessProvider = libraryAccessProvider;
        _logger = logger;
    }

    public bool IsPathAuthorized(string filePath, IEnumerable<string> libraryPaths)
    {
        if (string.IsNullOrWhiteSpace(filePath) || libraryPaths == null || !libraryPaths.Any())
        {
            return false;
        }

        // Security (audit H2): reject paths that could inject arguments into ffmpeg/ffprobe
        // (a double-quote / control char). Defense-in-depth for user-facing stream/transcode
        // flows and for any item that predates the scan-time guard.
        if (SoftMedia.Server.Helpers.MediaPathSafety.HasArgumentInjectionRisk(filePath))
        {
            _logger.LogWarning("Access denied: path contains unsafe characters: {FilePath}", filePath);
            return false;
        }

        try
        {
            // SDD §6.2: canonicalisation MUST resolve symlinks in addition to collapsing `..`.
            // Path.GetFullPath alone is insufficient on Linux — a symlink under an admin-
            // declared library root would otherwise re-introduce LFI.
            var canonicalFilePath = ResolveRealPath(filePath);

            foreach (var libPath in libraryPaths)
            {
                var canonicalLibPath = ResolveRealPath(libPath);

                if (!canonicalLibPath.EndsWith(Path.DirectorySeparatorChar))
                {
                    canonicalLibPath += Path.DirectorySeparatorChar;
                }

                if (canonicalFilePath.StartsWith(canonicalLibPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            _logger.LogWarning("Access denied: File '{FilePath}' is not within any authorized library paths.", filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating path security for file '{FilePath}'.", filePath);
            return false;
        }
    }

    // Resolve symlinks at EVERY path component (not just the leaf) and collapse `..`.
    // Path.GetFullPath collapses `..` but does NOT follow symlinks, and ResolveLinkTarget only
    // resolves a component that is ITSELF a link — so a leaf-only resolve misses an intermediate
    // symlinked directory (audit wave-2 M-7): a symlink dropped inside a library (e.g. lib/sub ->
    // /etc) would otherwise satisfy the StartsWith(lib) jail and re-introduce LFI. We walk the
    // path root -> leaf, resolving each component, so the final value is the true on-disk target.
    // Library roots are canonicalised the same way because an admin may add a symlinked root.
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return full;

            var current = root;
            var segments = full.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                FileSystemInfo? info = File.Exists(current)
                    ? new FileInfo(current)
                    : Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : null;

                // ResolveLinkTarget(true) walks the whole symlink chain (throwing on a cycle);
                // a non-link component returns null and is kept as-is. Resolving onto `current`
                // each step means subsequent segments build on the already-resolved location.
                var resolved = info?.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (!string.IsNullOrEmpty(resolved))
                {
                    current = resolved;
                }
            }

            return current;
        }
        catch
        {
            // Broken/cyclic link or odd path — fall back to the lexical full path. The caller's
            // StartsWith check then rejects anything escaping, and a broken link won't open anyway.
            return full;
        }
    }

    public async Task<MediaAccessResult> ValidateMediaAccessAsync(MediaItem? item)
    {
        if (item == null)
        {
            return MediaAccessResult.FileNotFound; // Logic: Item not found implies file interaction impossible
        }

        if (item.Library == null)
        {
             _logger.LogWarning("Validation failed: Media item {Id} has no associated library.", item.Id);
             return MediaAccessResult.FileNotFound; // Treat broken library link as not found
        }

        if (string.IsNullOrEmpty(item.Path))
        {
             return MediaAccessResult.FileNotFound;
        }

        if (!File.Exists(item.Path))
        {
            _logger.LogWarning("Validation failed: File not found on disk: {Path}", item.Path);
            return MediaAccessResult.FileNotFound;
        }

        if (!IsPathAuthorized(item.Path, item.Library.Paths))
        {
            _logger.LogWarning("Validation failed: LFI attempt blocked for {Path}", item.Path);
            return MediaAccessResult.Unauthorized;
        }

        // Wave C — per-user library ACL gate. Controllers map Unauthorized to
        // 404 (not 403) per SDD §6.2's anti-probe rule. Admins always have
        // LibraryAccess.Unrestricted, so they short-circuit out.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        if (!access.IsUnrestricted && !access.AllowedLibraryIds.Contains(item.LibraryId))
        {
            _logger.LogInformation(
                "Validation failed: library ACL blocks user from media {Id} in library {LibraryId}",
                item.Id, item.LibraryId);
            return MediaAccessResult.Unauthorized;
        }

        return MediaAccessResult.Allowed;
    }
}
