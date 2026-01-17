using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class MetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IMetadataRouter _metadataRouter;
    private readonly ISettingsService _settingsService;
    private readonly ImageCacheService _imageCacheService;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        ImageCacheService imageCacheService,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageCacheService = imageCacheService;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false)
    {
        if (type == LibraryType.Music)
        {
             await EnrichMusicItemAsync(item, deferImageCaching);
             return;
        }

        try
        {
            // Use MetadataRouter to get metadata from the user's preferred provider
            var json = await _metadataRouter.FetchMetadataAsync(item, type);
            if (string.IsNullOrEmpty(json)) return;

            item.MetadataJson = json;

            await ProcessMetadataJsonAsync(item, json, deferImageCaching);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }

    private async Task EnrichMusicItemAsync(MediaItem item, bool deferImageCaching = false)
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
                await ProcessMetadataJsonAsync(item, finalJson, deferImageCaching);
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
            await ProcessMetadataJsonAsync(item, finalJson, deferImageCaching);
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
            await ProcessMetadataJsonAsync(item, json);

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

    private async Task ProcessMetadataJsonAsync(MediaItem item, string json, bool deferImageCaching = false)
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
            
            if (metadata.TryGetValue("contentRating", out var contentRatingObj))
            {
                item.ContentRating = contentRatingObj.ToString();
            }
                
            if (metadata.TryGetValue("releaseDate", out var releaseDateObj) && DateTime.TryParse(releaseDateObj.ToString(), out var releaseDate))
            {
                item.ReleaseDate = releaseDate;
            }
            
            // If deferring image caching, skip all image download operations
            // Background service will handle image caching later
            if (deferImageCaching)
            {
                return;
            }
            
            // Cache poster image locally if available
            if (metadata.TryGetValue("poster", out var posterObj))
            {
                var posterUrl = posterObj.ToString();
                _logger.LogInformation("Found poster URL for {Title}: {Url}, ItemType: {Type}", item.Title, posterUrl, item.Type);
                
                if (!string.IsNullOrEmpty(posterUrl) && posterUrl.StartsWith("http"))
                {
                    try
                    {
                        _logger.LogInformation("Caching poster for {Title} (Type: {Type})", item.Title, item.Type);
                        string cachedUrl = item.Type switch
                        {
                            MediaType.Movie => await _imageCacheService.CacheMoviePosterAsync(item.Id, posterUrl),
                            MediaType.Series => await _imageCacheService.CacheSeriesPosterAsync(item.Id, posterUrl),
                            MediaType.Audio => await _imageCacheService.CacheAlbumCoverAsync(item.Id, posterUrl),
                            _ => posterUrl
                        };
                        
                        if (cachedUrl != posterUrl)
                        {
                            // Update metadata with cached URL
                            metadata["poster"] = cachedUrl;
                            item.MetadataJson = JsonSerializer.Serialize(metadata);
                            _logger.LogDebug("Cached poster for {Title}: {Url}", item.Title, cachedUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cache poster for {Title}", item.Title);
                    }
                }
            }
            
            // For Series: Cache season posters and episode stills, update metadata with cached URLs
            if (item.Type == MediaType.Series)
            {
                bool metadataModified = false;
                
                // Cache season posters and update URLs
                if (metadata.TryGetValue("seasons", out var seasonsObj) && seasonsObj is JsonElement seasonsArray)
                {
                    // Convert to mutable list of dictionaries
                    var seasonsList = new List<Dictionary<string, object?>>();
                    foreach (var season in seasonsArray.EnumerateArray())
                    {
                        var seasonDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(season.GetRawText()) ?? new();
                        
                        try
                        {
                            if (seasonDict.TryGetValue("number", out var numObj) && numObj != null &&
                                seasonDict.TryGetValue("poster", out var seasonPosterObj) && seasonPosterObj != null)
                            {
                                var seasonNum = Convert.ToInt32(numObj.ToString());
                                var seasonPosterUrl = seasonPosterObj.ToString();
                                
                                if (!string.IsNullOrEmpty(seasonPosterUrl) && seasonPosterUrl.StartsWith("http"))
                                {
                                    var cachedUrl = await _imageCacheService.CacheSeasonPosterAsync(item.Id, seasonNum, seasonPosterUrl);
                                    if (cachedUrl != seasonPosterUrl)
                                    {
                                        seasonDict["poster"] = cachedUrl;
                                        metadataModified = true;
                                        _logger.LogDebug("Cached season {Season} poster for {Title}: {Url}", seasonNum, item.Title, cachedUrl);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to cache season poster for {Title}", item.Title);
                        }
                        
                        seasonsList.Add(seasonDict);
                    }
                    
                    if (metadataModified)
                    {
                        metadata["seasons"] = seasonsList;
                    }
                }
                
                // Cache episode stills and update URLs
                if (metadata.TryGetValue("episodes", out var episodesObj) && episodesObj is JsonElement episodesArray)
                {
                    var episodesList = new List<Dictionary<string, object?>>();
                    bool episodesModified = false;
                    
                    foreach (var episode in episodesArray.EnumerateArray())
                    {
                        var epDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(episode.GetRawText()) ?? new();
                        
                        try
                        {
                            if (epDict.TryGetValue("season", out var seasonObj) &&
                                epDict.TryGetValue("episode", out var epNumObj) &&
                                epDict.TryGetValue("still", out var stillObj) && stillObj != null)
                            {
                                var epSeason = seasonObj != null ? Convert.ToInt32(seasonObj.ToString()) : 0;
                                var epNum = epNumObj != null ? Convert.ToInt32(epNumObj.ToString()) : 0;
                                var stillUrl = stillObj.ToString();
                                
                                if (epNum > 0 && !string.IsNullOrEmpty(stillUrl) && stillUrl.StartsWith("http"))
                                {
                                    var cachedUrl = await _imageCacheService.CacheEpisodeStillAsync(item.Id, epSeason, epNum, stillUrl);
                                    if (cachedUrl != stillUrl)
                                    {
                                        epDict["still"] = cachedUrl;
                                        episodesModified = true;
                                        _logger.LogDebug("Cached S{Season}E{Episode} still for {Title}: {Url}", epSeason, epNum, item.Title, cachedUrl);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to cache episode still for {Title}", item.Title);
                        }
                        
                        episodesList.Add(epDict);
                    }
                    
                    if (episodesModified)
                    {
                        metadata["episodes"] = episodesList;
                        metadataModified = true;
                    }
                }
                
                // Save updated metadata back to item
                if (metadataModified)
                {
                    item.MetadataJson = JsonSerializer.Serialize(metadata);
                    _logger.LogInformation("Updated metadata with cached image URLs for {Title}", item.Title);
                }
            }
        }
    }
}
