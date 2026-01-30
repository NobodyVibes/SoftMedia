using SoftMedia.Server.Services.Abstractions;

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
                // (e.g., "C:\Media" matching "C:\MediaDocs")
                // Although StartsWith comparison usually handles this if we are careful, 
                // getting full path is the most important part.
                
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
}
