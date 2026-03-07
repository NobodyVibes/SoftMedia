using SoftMedia.Server.Models;
using System.Text.Json;
using MetadataExtractor;

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
        var title = item.Title;
        var path = item.Path;
        try
        {
            if (!File.Exists(path)) return Task.FromResult<MetadataResult?>(null);

            var directories = ImageMetadataReader.ReadMetadata(path);
            var metadata = new MetadataResult();
            var extraData = new Dictionary<string, string>();

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
                extraData["camera"] = $"{make} {model}".Trim();
            }

            var iso = GetTagValue("ISO Speed Ratings");
            if (!string.IsNullOrEmpty(iso)) extraData["iso"] = iso;

            var fnumber = GetTagValue("F-Number");
            if (!string.IsNullOrEmpty(fnumber)) extraData["fstop"] = fnumber;

            var exposure = GetTagValue("Exposure Time");
            if (!string.IsNullOrEmpty(exposure)) extraData["exposure"] = exposure;

            var dateTaken = GetTagValue("Date/Time Original");
            if (!string.IsNullOrEmpty(dateTaken) && DateTime.TryParseExact(dateTaken, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                extraData["dateTaken"] = date.ToString("yyyy-MM-dd HH:mm:ss");
                metadata.Year = date.Year;
            }

            // GPS
            var lat = GetTagValue("GPS Latitude");
            var lon = GetTagValue("GPS Longitude");
            if (!string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon))
            {
                extraData["gps"] = $"{lat}, {lon}";
            }

            if (extraData.Count > 0)
            {
                metadata.Extra = new Dictionary<string, JsonElement>();
                foreach (var kvp in extraData)
                {
                    metadata.Extra[kvp.Key] = JsonSerializer.SerializeToElement(kvp.Value);
                }
            }

            return Task.FromResult<MetadataResult?>(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading EXIF for {path}");
            return Task.FromResult<MetadataResult?>(null);
        }
    }
}
