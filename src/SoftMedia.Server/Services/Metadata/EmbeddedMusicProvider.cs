using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class EmbeddedMusicProvider : IMetadataProvider
{
    private readonly ILogger<EmbeddedMusicProvider> _logger;

    public LibraryType SupportedType => LibraryType.Music;
    public string ProviderName => "Embedded";

    public EmbeddedMusicProvider(ILogger<EmbeddedMusicProvider> logger)
    {
        _logger = logger;
    }

    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;

        // Optimization: If MusicScanner already extracted all tags (indicated by "scannedTags": true),
        // we can just deserialize the existing MetadataJson and return it, skipping the TagLib read entirely.
        if (!string.IsNullOrEmpty(item.MetadataJson) && item.MetadataJson.Contains("\"scannedTags\""))
        {
            try
            {
                var existingResult = JsonSerializer.Deserialize<MetadataResult>(item.MetadataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (existingResult != null)
                {
                    _logger.LogInformation("Using pre-scanned metadata from MusicScanner for {Path}", path);
                    return Task.FromResult<MetadataResult?>(existingResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize pre-scanned MetadataResult for {Path}. Falling back to TagLib.", path);
            }
        }

        // Fallback: Legacy item or scanner didn't fully scan it
        var result = new MetadataResult();
        bool hasData = false;
        try
        {
            if (File.Exists(path))
            {
                using var tfile = TagLib.File.Create(path);
                var tag = tfile.Tag;

                if (!string.IsNullOrEmpty(tag.Title)) { result.Title = tag.Title; hasData = true; }
                if (!string.IsNullOrEmpty(tag.FirstPerformer)) { result.Artist = tag.FirstPerformer; hasData = true; }
                if (!string.IsNullOrEmpty(tag.Album)) { result.Album = tag.Album; hasData = true; }
                if (tag.Year > 0) { result.Year = (int)tag.Year; hasData = true; }
                if (tag.Genres.Length > 0) { result.Genres = tag.Genres.ToList(); hasData = true; }
                if (tag.Track > 0) { result.TrackNumber = (int)tag.Track; hasData = true; }
                if (tag.Disc > 0) { result.DiscNumber = (int)tag.Disc; hasData = true; }

                // Duration from file properties
                result.Duration = tfile.Properties.Duration.TotalSeconds;

                // Check for embedded pictures
                if (tfile.Tag.Pictures.Length > 0)
                {
                    _logger.LogInformation("Found {Count} embedded pictures in {Path}. MimeType: {MimeType}", tfile.Tag.Pictures.Length, path, tfile.Tag.Pictures[0].MimeType);
                    result.HasEmbeddedArt = true;
                    hasData = true;
                }
                else
                {
                    _logger.LogInformation("No embedded pictures found in {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading embedded tags for {Path}", path);
        }

        return Task.FromResult(hasData ? result : null);
    }
}
