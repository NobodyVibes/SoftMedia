using MetadataExtractor;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// EXIF fields extracted from a photo file. <see cref="Fields"/> holds the display-only
/// dictionary persisted to <c>MediaItem.ExifJson</c> (camera, iso, fstop, exposure, gps,
/// dateTaken); <see cref="DateTaken"/>/<see cref="Year"/> are promoted separately so the
/// caller can populate the queryable ReleaseDate/Year columns.
/// </summary>
public record PhotoExifData(int? Year, DateTime? DateTaken, Dictionary<string, string> Fields);

/// <summary>
/// Shared EXIF extraction for photos. Used inline by PhotoScanner at scan time (so a photo
/// library never round-trips through the metadata queue) and by ExifMetadataProvider for
/// the manual-refresh path — one implementation, two entry points.
/// </summary>
public static class PhotoExifReader
{
    /// <summary>
    /// Reads EXIF metadata from an image file. Returns null when the file is missing or
    /// unreadable; returns an empty-Fields result when the image simply carries no EXIF.
    /// </summary>
    public static PhotoExifData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var directories = ImageMetadataReader.ReadMetadata(path);
            var fields = new Dictionary<string, string>();
            int? year = null;
            DateTime? dateTaken = null;

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
                fields["camera"] = $"{make} {model}".Trim();
            }

            var iso = GetTagValue("ISO Speed Ratings");
            if (!string.IsNullOrEmpty(iso)) fields["iso"] = iso;

            var fnumber = GetTagValue("F-Number");
            if (!string.IsNullOrEmpty(fnumber)) fields["fstop"] = fnumber;

            var exposure = GetTagValue("Exposure Time");
            if (!string.IsNullOrEmpty(exposure)) fields["exposure"] = exposure;

            var rawDate = GetTagValue("Date/Time Original");
            if (!string.IsNullOrEmpty(rawDate) && DateTime.TryParseExact(rawDate, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                fields["dateTaken"] = date.ToString("yyyy-MM-dd HH:mm:ss");
                dateTaken = date;
                year = date.Year;
            }

            var lat = GetTagValue("GPS Latitude");
            var lon = GetTagValue("GPS Longitude");
            if (!string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon))
            {
                fields["gps"] = $"{lat}, {lon}";
            }

            return new PhotoExifData(year, dateTaken, fields);
        }
        catch
        {
            // Corrupt/truncated image or unsupported container — the photo still scans,
            // it just carries no EXIF card data. Callers log at their own level.
            return null;
        }
    }
}
