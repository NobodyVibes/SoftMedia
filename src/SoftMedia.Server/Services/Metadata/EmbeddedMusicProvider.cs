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

    public Task<string?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;
        var metadata = new Dictionary<string, object>();
        try
        {
            if (File.Exists(path))
            {
                using var tfile = TagLib.File.Create(path);
                var tag = tfile.Tag;

                if (!string.IsNullOrEmpty(tag.Title)) metadata["title"] = tag.Title;
                if (!string.IsNullOrEmpty(tag.FirstPerformer)) metadata["artist"] = tag.FirstPerformer;
                if (!string.IsNullOrEmpty(tag.Album)) metadata["album"] = tag.Album;
                if (tag.Year > 0) metadata["year"] = tag.Year;
                if (tag.Genres.Length > 0) metadata["genres"] = tag.Genres;
                if (tag.Track > 0) metadata["track"] = tag.Track;
                if (tag.Disc > 0) metadata["disc"] = tag.Disc;

                // Duration from file properties
                metadata["duration"] = tfile.Properties.Duration.TotalSeconds;

                // Check for embedded pictures
                if (tfile.Tag.Pictures.Length > 0)
                {
                    _logger.LogInformation("Found {Count} embedded pictures in {Path}. MimeType: {MimeType}", tfile.Tag.Pictures.Length, path, tfile.Tag.Pictures[0].MimeType);
                    metadata["hasEmbeddedArt"] = true;
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

        return Task.FromResult(metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null);
    }
}
