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
        try
        {
            if (!File.Exists(path)) return Task.FromResult<string?>(null);

            var directories = ImageMetadataReader.ReadMetadata(path);
            var metadata = new Dictionary<string, object>();

            // Helper to find tag value across directories
            string? GetTagValue(string tagName)
            {
                foreach (var directory in directories)
                {
                    var tag = directory.Tags.FirstOrDefault(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
                    if (tag != null) return tag.Description;
                }
                return null;
            }

            var make = GetTagValue("Make");
            var model = GetTagValue("Model");
            if (!string.IsNullOrEmpty(make) || !string.IsNullOrEmpty(model))
            {
                metadata["camera"] = $"{make} {model}".Trim();
            }

            var iso = GetTagValue("ISO Speed Ratings");
            if (!string.IsNullOrEmpty(iso)) metadata["iso"] = iso;

            var fnumber = GetTagValue("F-Number");
            if (!string.IsNullOrEmpty(fnumber)) metadata["fstop"] = fnumber;

            var exposure = GetTagValue("Exposure Time");
            if (!string.IsNullOrEmpty(exposure)) metadata["exposure"] = exposure;

            var dateTaken = GetTagValue("Date/Time Original");
            if (!string.IsNullOrEmpty(dateTaken) && DateTime.TryParseExact(dateTaken, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                metadata["dateTaken"] = date.ToString("yyyy-MM-dd HH:mm:ss");
                metadata["year"] = date.Year;
            }

            // GPS
            var lat = GetTagValue("GPS Latitude");
            var lon = GetTagValue("GPS Longitude");
            if (!string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon))
            {
                metadata["gps"] = $"{lat}, {lon}";
            }

            return Task.FromResult<string?>(JsonSerializer.Serialize(metadata));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading EXIF for {path}");
            return Task.FromResult<string?>(null);
        }
    }
}
