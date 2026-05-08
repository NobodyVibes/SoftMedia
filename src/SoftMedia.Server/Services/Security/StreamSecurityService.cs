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

    // Resolve symlinks AND collapse `..`. ResolveLinkTarget(true) returns the final
    // target by walking the symlink chain; if the path is not a symlink, it returns
    // null and we fall back to GetFullPath. Library roots are themselves checked
    // because admins may add a symlinked directory as a root.
    private static string ResolveRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            FileSystemInfo? info = File.Exists(fullPath)
                ? new FileInfo(fullPath)
                : Directory.Exists(fullPath)
                    ? new DirectoryInfo(fullPath)
                    : null;

            var resolved = info?.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return resolved ?? fullPath;
        }
        catch
        {
            // ResolveLinkTarget can throw on broken/cyclic links — treat as the literal
            // path; the caller's StartsWith check will then reject anything escaping.
            return fullPath;
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
