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
        // This provider only handles audio tracks (files with embedded ID3/Vorbis tags).
        // Albums and Artists are directories — TagLib cannot process them.
        if (item.Type != MediaType.Audio)
        {
            return Task.FromResult<MetadataResult?>(null);
        }

        var title = item.Title;
        var path = item.Path;

        // Optimization: If the item was recently added by MusicScanner during an initial scan,
        // it already extracted the base tags and populated the DB. We can skip hitting TagLib again.
        if (item.DateAdded >= DateTime.UtcNow.AddMinutes(-5))
        {
            _logger.LogInformation("Skipping redundant TagLib read for freshly scanned item {Path}", path);
            return Task.FromResult<MetadataResult?>(null);
        }

        // Fallback: Legacy item or scanner didn't fully scan it
        var result = new MetadataResult();
        bool hasData = false;
        try
        {
            if (File.Exists(path))
            {
                _logger.LogWarning("[EmbeddedMusicProvider] TagLib fallback triggered for item: {Title}. This indicates a legacy format missing 'scannedTags' flag. Path: {Path}", title, path);

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
