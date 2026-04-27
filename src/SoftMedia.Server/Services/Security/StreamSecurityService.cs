using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security;

public class StreamSecurityService : IStreamSecurityService
{
    private readonly ILogger<StreamSecurityService> _logger;

    public StreamSecurityService(ILogger<StreamSecurityService> logger)
    {
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
            var canonicalFilePath = Path.GetFullPath(filePath);

            foreach (var libPath in libraryPaths)
            {
                var canonicalLibPath = Path.GetFullPath(libPath);
                
                // Ensure the library path ends with a separator to prevent partial matches 
                // unless it is the root drive
                if (!canonicalLibPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
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

    public MediaAccessResult ValidateMediaAccess(MediaItem? item)
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

        return MediaAccessResult.Allowed;
    }
}
