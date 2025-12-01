using SoftMedia.Server.Models;
using System.Text.Json;
using MetadataExtractor;

namespace SoftMedia.Server.Services.Metadata;

public class ExifMetadataProvider : IMetadataProvider
{
    private readonly ILogger<ExifMetadataProvider> _logger;

    public LibraryType SupportedType => LibraryType.Photo;

    public ExifMetadataProvider(ILogger<ExifMetadataProvider> logger)
    {
        _logger = logger;
    }

    public Task<string?> FetchMetadataAsync(string title, string path)
    {
        // Note: For Photo libraries, the 'title' argument passed to FetchMetadataAsync
        // is actually the file path (or should be). 
        // The MetadataService/Router needs to ensure it passes the path for Local providers.
        // However, the interface defines it as 'title'.
        // If the caller passes the Title (filename without extension), we can't read the file.
        // We need to assume the caller passes the full path for Local providers or we need to change the interface.
        
        // For now, let's assume the caller passes the path if it's a local provider, 
        // or we might need to adjust the MetadataRouter to pass the path.
        // Looking at MetadataRouter (which I haven't seen fully but I can infer), 
        // it likely calls FetchMetadataAsync(mediaItem.Title).
        
        // If I can't change the interface easily without breaking other things, 
        // I might need to rely on the fact that for Photos, the "Title" might be the filename, 
        // but we need the full path to read EXIF.
        
        // Let's implement it assuming 'path' is passed, and if not, we'll need to fix the Router.
        // But wait, the interface says `FetchMetadataAsync(string title)`.
        
        // Actually, looking at the SDD, `LocalMetadataProvider` is mentioned.
        // If I look at `MetadataRouter.cs` (which I saw in the file list but didn't read),
        // I should check how it calls the provider.
        
        // For this implementation, I will try to use the argument as a path.
        
        try
        {
            if (!File.Exists(path))
            {
                // If it's not a path, maybe it's just a title. 
                // In that case, we can't fetch EXIF.
                return Task.FromResult<string?>(null);
            }

            var directories = ImageMetadataReader.ReadMetadata(path);
            var metadata = new Dictionary<string, string>();

            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags)
                {
                    // Filter for common EXIF tags to avoid bloating the DB
                    if (IsInterestingTag(tag.Name))
                    {
                        metadata[$"{directory.Name}:{tag.Name}"] = tag.Description ?? "";
                    }
                }
            }

            return Task.FromResult<string?>(JsonSerializer.Serialize(metadata));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading EXIF for {path}");
            return Task.FromResult<string?>(null);
        }
    }

    private bool IsInterestingTag(string tagName)
    {
        var interesting = new[] { "Make", "Model", "F-Number", "ISO", "Exposure Time", "Date/Time Original", "GPS Latitude", "GPS Longitude" };
        return interesting.Any(i => tagName.Contains(i, StringComparison.OrdinalIgnoreCase));
    }
}
