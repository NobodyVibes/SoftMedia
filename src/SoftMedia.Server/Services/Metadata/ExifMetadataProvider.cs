using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class ExifMetadataProvider : IMetadataProvider
{
    private readonly ILogger<ExifMetadataProvider> _logger;

    public LibraryType SupportedType => LibraryType.Photo;
    public string ProviderName => "Exif";

    public ExifMetadataProvider(ILogger<ExifMetadataProvider> logger)
    {
        _logger = logger;
    }

    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Extraction lives in PhotoExifReader, shared with PhotoScanner's inline scan-time
        // read — this provider is the manual-refresh path through the metadata queue.
        var exif = PhotoExifReader.TryRead(item.Path);
        if (exif == null)
        {
            _logger.LogWarning("Could not read EXIF metadata for {Path}", item.Path);
            return Task.FromResult<MetadataResult?>(null);
        }

        var metadata = new MetadataResult
        {
            Year = exif.Year,
            ReleaseDate = exif.DateTaken,
        };

        // Photo-specific EXIF fields (camera, iso, fstop, exposure, gps, dateTaken)
        // remain in Extra by design — they are display-only and do not require
        // relational querying. MetadataAggregator persists them to MediaItem.ExifJson.
        if (exif.Fields.Count > 0)
        {
            metadata.Extra = new Dictionary<string, JsonElement>();
            foreach (var kvp in exif.Fields)
            {
                metadata.Extra[kvp.Key] = JsonSerializer.SerializeToElement(kvp.Value);
            }
        }

        return Task.FromResult<MetadataResult?>(metadata);
    }
}
