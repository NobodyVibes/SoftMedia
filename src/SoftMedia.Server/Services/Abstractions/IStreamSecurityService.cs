using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public enum MediaAccessResult
{
    Allowed,
    FileNotFound,
    Unauthorized
}

public interface IStreamSecurityService
{
    /// <summary>
    /// Validates if a file path is allowed to be accessed based on the library's configured paths.
    /// Prevents Local File Inclusion (LFI).
    /// </summary>
    bool IsPathAuthorized(string filePath, IEnumerable<string> libraryPaths);

    /// <summary>
    /// Validates that the media item's file exists and is within authorized library paths.
    /// </summary>
    MediaAccessResult ValidateMediaAccess(MediaItem item);
}
