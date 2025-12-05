using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class MusicBrainzProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzProvider> _logger;
    
    // MusicBrainz requires 1 request per second.
    // Using a static semaphore to enforce this across all scoped instances.
    private static readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public LibraryType SupportedType => LibraryType.Music;

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // User-Agent is MANDATORY for MusicBrainz
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public Task<string?> FetchMetadataAsync(string title, string path)
    {
        // 1. Try to read embedded tags first (Fast, Local)
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading tags for {Path}", path);
        }

        // 2. If we have enough info, we could query MusicBrainz (Optional)
        // For now, returning local tags is a huge win.
        // We can add API fetching later if needed, but rate limits make it tricky for 1000s of tracks.
        
        return Task.FromResult(metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null);
    }
}
