namespace SoftMedia.Server.Services.Abstractions;

public interface IStreamSecurityService
{
    /// <summary>
    /// Validates if a file path is allowed to be accessed based on the library's configured paths.
    /// Prevents Local File Inclusion (LFI).
    /// </summary>
    /// <param name="filePath">The absolute path to the file.</param>
    /// <param name="libraryPaths">The list of authorized root paths for the library.</param>
    /// <returns>True if access is authorized; otherwise, false.</returns>
    bool IsPathAuthorized(string filePath, IEnumerable<string> libraryPaths);
}
