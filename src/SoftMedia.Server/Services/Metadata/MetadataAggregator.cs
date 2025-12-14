using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class MetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(IEnumerable<IMetadataProvider> providers, ISettingsService settingsService, ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type)
    {
        if (type == LibraryType.Music)
        {
             await EnrichMusicItemAsync(item);
             return;
        }

        var provider = _providers.FirstOrDefault(p => p.SupportedType == type);
        if (provider == null) return;

        try
        {
            var json = await provider.FetchMetadataAsync(item);
            if (string.IsNullOrEmpty(json)) return;

            item.MetadataJson = json;

            ProcessMetadataJson(item, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }

    private async Task EnrichMusicItemAsync(MediaItem item)
    {
        var primaryName = await _settingsService.GetSettingAsync("MusicProviderPrimary", "Embedded");
        var fallbackName = await _settingsService.GetSettingAsync("MusicProviderFallback", "MusicBrainz");

        var providers = _providers.Where(p => p.SupportedType == LibraryType.Music).ToList();
        
        var primary = providers.FirstOrDefault(p => p.ProviderName.Equals(primaryName, StringComparison.OrdinalIgnoreCase)) 
                      ?? providers.FirstOrDefault(p => p.ProviderName == "Embedded");
        
        var fallback = providers.FirstOrDefault(p => p.ProviderName.Equals(fallbackName, StringComparison.OrdinalIgnoreCase))
                       ?? providers.FirstOrDefault(p => p.ProviderName == "MusicBrainz");

        Dictionary<string, object>? primaryData = null;
        if (primary != null)
        {
             try 
             {
                 var json = await primary.FetchMetadataAsync(item);
                 if (!string.IsNullOrEmpty(json))
                 {
                     // IMPORTANT: Save primary data to item context so fallback can read it!
                     item.MetadataJson = json;
                     primaryData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                 }
             }
             catch (Exception ex)
             {
                 _logger.LogError(ex, "Error fetching from {Provider}", primary.ProviderName);
             }
        }

        // Check sufficiency: Needs Title, Artist at minimum. 
        // AND for it to be fully sufficient to skip fallback, it needs Artwork (embedded or url).
        bool sufficient = false;
        if (primaryData != null)
        {
             if (primaryData.ContainsKey("title") && primaryData.ContainsKey("artist"))
             {
                 // Check for art
                 if (primaryData.ContainsKey("hasEmbeddedArt") || primaryData.ContainsKey("poster"))
                 {
                     sufficient = true;
                 }
             }
        }

        if (sufficient || fallback == null || fallback == primary)
        {
            // Just use primary
            if (primaryData != null)
            {
                var finalJson = JsonSerializer.Serialize(primaryData);
                item.MetadataJson = finalJson;
                ProcessMetadataJson(item, finalJson);
            }
            return;
        }

        // Fetch Fallback
        Dictionary<string, object>? fallbackData = null;
        try 
        {
             // Use Title/Artist from primary if available via item.MetadataJson
             var json = await fallback.FetchMetadataAsync(item);
             if (!string.IsNullOrEmpty(json))
             {
                 fallbackData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
             }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error fetching from {Provider}", fallback.ProviderName);
        }

        // Merge Strategy: Primary wins on tags, Fallback fills gaps (especially poster)
        var mergedData = primaryData ?? new Dictionary<string, object>();
        
        if (fallbackData != null)
        {
            // If primary was empty, take everything
            if (primaryData == null)
            {
                mergedData = fallbackData;
            }
            else
            {
                // Merge specific fields if missing in primary
                if (!mergedData.ContainsKey("poster") && fallbackData.TryGetValue("poster", out var poster)) mergedData["poster"] = poster;
                if (!mergedData.ContainsKey("year") && fallbackData.TryGetValue("year", out var year)) mergedData["year"] = year;
                if (!mergedData.ContainsKey("album") && fallbackData.TryGetValue("album", out var album)) mergedData["album"] = album;
                if (!mergedData.ContainsKey("genres") && fallbackData.TryGetValue("genres", out var genres)) mergedData["genres"] = genres;
                
                // If primary didn't even have title/artist (unlikely if we are here via broken sufficiency check, but possible), take them
                if (!mergedData.ContainsKey("title") && fallbackData.TryGetValue("title", out var title)) mergedData["title"] = title;
                if (!mergedData.ContainsKey("artist") && fallbackData.TryGetValue("artist", out var artist)) mergedData["artist"] = artist;
            }
        }

        if (mergedData.Count > 0)
        {
            var finalJson = JsonSerializer.Serialize(mergedData);
            item.MetadataJson = finalJson;
            ProcessMetadataJson(item, finalJson);
        }
    }

    private async Task<bool> TryApplyMetadata(MediaItem item, IMetadataProvider provider)
    {
        // Deprecated for Music, kept for others if needed or remove if unused.
        // Actually generic enrich still uses this.
        try 
        {
            var json = await provider.FetchMetadataAsync(item);
            if (string.IsNullOrEmpty(json)) return false;

            item.MetadataJson = json;
            ProcessMetadataJson(item, json);

            var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (metadata != null)
            {
                 bool hasTitle = metadata.ContainsKey("title");
                 // For generic/video, maybe different sufficiency? 
                 // Current logic was: Title && Artist. 
                 // For generic check, we'll keep it simple or align with legacy.
                 // The original code checked specific keys.
                 return hasTitle;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching from {Provider} for {Item}", provider.ProviderName, item.Title);
            return false;
        }
    }

    private void ProcessMetadataJson(MediaItem item, string json)
    {
        // Parse and promote fields
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        if (metadata != null)
        {
            if (metadata.TryGetValue("year", out var yearObj) && int.TryParse(yearObj.ToString(), out var year))
            {
                item.Year = year;
            }

            if (metadata.TryGetValue("description", out var descObj))
            {
                item.Overview = descObj.ToString();
            }
            
            // Removed CommunityRating population from external metadata to separate internal/external ratings
            // if (metadata.TryGetValue("rating", out var ratingObj) && double.TryParse(ratingObj.ToString(), out var rating))
            // {
            //     item.CommunityRating = rating;
            // }
            
                if (metadata.TryGetValue("contentRating", out var contentRatingObj))
            {
                item.ContentRating = contentRatingObj.ToString();
            }
                
                if (metadata.TryGetValue("releaseDate", out var releaseDateObj) && DateTime.TryParse(releaseDateObj.ToString(), out var releaseDate))
            {
                item.ReleaseDate = releaseDate;
            }
        }
    }
}
