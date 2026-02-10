using SoftMedia.Server.Models;
using System.Text.Json;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataAggregator
{
    Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true);
}

public class MetadataAggregator : IMetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IMetadataRouter _metadataRouter;
    private readonly ISettingsService _settingsService;
    private readonly IImageDownloadQueue _imageDownloadQueue;
    private readonly AppDbContext _context;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        IImageDownloadQueue imageDownloadQueue,
        AppDbContext context,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageDownloadQueue = imageDownloadQueue;
        _context = context;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true)
    {
        if (type == LibraryType.Music)
        {
             await EnrichMusicItemAsync(item, deferImageCaching, refreshImages);
             return;
        }

        try
        {
            // Use MetadataRouter to get metadata from the user's preferred provider
            var json = await _metadataRouter.FetchMetadataAsync(item, type);
            if (string.IsNullOrEmpty(json)) return;

            // MERGE Logic: Preserve existing technical metadata (chapters, creditsStart)
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
                    var newMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                    if (existing != null && newMeta != null)
                    {
                        // Update existing with new values (External metadata wins for promoted fields)
                        foreach (var kvp in newMeta)
                        {
                            existing[kvp.Key] = kvp.Value;
                        }
                        
                        // Re-serialize
                        json = JsonSerializer.Serialize(existing);
                    }
                }
                catch
                {
                    // If merge fails, fall back to overwrite but log warning
                    _logger.LogWarning("Failed to merge metadata for {Title}, overwriting.", item.Title);
                }
            }

            item.MetadataJson = json;

            await ProcessMetadataJsonAsync(item, json, deferImageCaching, refreshImages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }

    private async Task EnrichMusicItemAsync(MediaItem item, bool deferImageCaching = false, bool refreshImages = true)
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
                await ProcessMetadataJsonAsync(item, finalJson, deferImageCaching, refreshImages);
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
            await ProcessMetadataJsonAsync(item, finalJson, deferImageCaching, refreshImages);
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

    private async Task ProcessMetadataJsonAsync(MediaItem item, string json, bool deferImageCaching = false, bool refreshImages = true)
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

            // Promote Ratings to CommunityRating column
            if (metadata.TryGetValue("imdbRating", out var imdbRatingObj) && double.TryParse(imdbRatingObj.ToString(), out var imdbRating))
            {
                item.CommunityRating = imdbRating;
            }
            else if (metadata.TryGetValue("rating", out var ratingObj) && double.TryParse(ratingObj.ToString(), out var genericRating))
            {
                item.CommunityRating = genericRating;
            }
                
            if (metadata.TryGetValue("releaseDate", out var releaseDateObj) && DateTime.TryParse(releaseDateObj.ToString(), out var releaseDate))
            {
                item.ReleaseDate = releaseDate;
            }
            
            // Populate External IDs if available (New Schema)
            if (metadata.TryGetValue("imdbId", out var imdbIdObj)) item.ImdbId = imdbIdObj.ToString();
            if (metadata.TryGetValue("tvmazeId", out var tvmazeIdObj) && int.TryParse(tvmazeIdObj.ToString(), out var tvmazeId)) item.TvMazeId = tvmazeId;
            if (metadata.TryGetValue("musicBrainzId", out var mbIdObj)) item.MusicBrainzId = mbIdObj.ToString();

            // FILTERING: Only keep metadata for Seasons/Episodes that exist locally
            if (item.Type == MediaType.Series)
            {
                // await FilterTvMetadataAsync(item, metadata);
                // Re-serialize the filtered metadata to update the JSON string being processed
                json = JsonSerializer.Serialize(metadata);
            }

            // If deferring image caching OR explicitly disabled, skip all image download operations
            // Background service will handle image caching later (if deferred) or never (if disabled)
            if (deferImageCaching || !refreshImages)
            {
                return;
            }

            // Queue for Image Downloads
            var downloads = new List<(string Url, int? Season, int? Episode, ImageType Type)>();

            // 1. Poster / Cover Art
            if (metadata.TryGetValue("poster", out var posterObj))
            {
                var posterUrl = posterObj.ToString();
                if (!string.IsNullOrEmpty(posterUrl) && posterUrl.StartsWith("http"))
                {
                    _logger.LogInformation("Found poster URL for {Title}: {Url}", item.Title, posterUrl);
                    downloads.Add((posterUrl, null, null, item.Type == MediaType.Audio || item.Type == MediaType.Album ? ImageType.AlbumCover : ImageType.Poster));
                    
                    // Remove remote URL to prevent hotlinking
                    metadata.Remove("poster");
                }
            }
            
            // 2. Series Specific Images
            if (item.Type == MediaType.Series)
            {
                // Season Posters
                if (metadata.TryGetValue("seasons", out var seasonsObj) && seasonsObj is JsonElement seasonsArray)
                {
                    var seasonsList = new List<Dictionary<string, object?>>();
                    foreach (var season in seasonsArray.EnumerateArray())
                    {
                        var seasonDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(season.GetRawText()) ?? new();
                        if (seasonDict.TryGetValue("number", out var numObj) && numObj != null &&
                            seasonDict.TryGetValue("poster", out var seasonPosterObj) && seasonPosterObj != null)
                        {
                            var seasonNum = Convert.ToInt32(numObj.ToString());
                            var seasonPosterUrl = seasonPosterObj.ToString();
                            
                            if (!string.IsNullOrEmpty(seasonPosterUrl) && seasonPosterUrl.StartsWith("http"))
                            {
                                downloads.Add((seasonPosterUrl, seasonNum, null, ImageType.SeasonPoster));
                                // Remove remote URL
                                seasonDict.Remove("poster");
                            }
                        }
                        seasonsList.Add(seasonDict);
                    }
                    metadata["seasons"] = seasonsList;
                }
                
                // Episode Stills
                if (metadata.TryGetValue("episodes", out var episodesObj) && episodesObj is JsonElement episodesArray)
                {
                    var episodesList = new List<Dictionary<string, object?>>();
                    foreach (var episode in episodesArray.EnumerateArray())
                    {
                        var epDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(episode.GetRawText()) ?? new();
                         if (epDict.TryGetValue("season", out var seasonObj) &&
                             epDict.TryGetValue("episode", out var epNumObj) &&
                             epDict.TryGetValue("still", out var stillObj) && stillObj != null)
                         {
                              var epSeason = seasonObj != null ? Convert.ToInt32(seasonObj.ToString()) : 0;
                              var epNum = epNumObj != null ? Convert.ToInt32(epNumObj.ToString()) : 0;
                              var stillUrl = stillObj.ToString();
                              
                              if (!string.IsNullOrEmpty(stillUrl) && stillUrl.StartsWith("http"))
                              {
                                   downloads.Add((stillUrl, epSeason, epNum, ImageType.Still));
                                   // Remove remote URL
                                   epDict.Remove("still");
                              }
                         }
                         episodesList.Add(epDict);
                    }
                    metadata["episodes"] = episodesList;
                }
            }

            // Update MetadataJson (Strip remote URLs)
            item.MetadataJson = JsonSerializer.Serialize(metadata);

            // SAVE to DB to prevent race conditions
            // We save the "clean" metadata first.
            await _context.SaveChangesAsync();

            // Enqueue Downloads
            foreach (var download in downloads)
            {
                 await _imageDownloadQueue.EnqueueImageDownloadAsync(
                     item.Id, 
                     download.Url, 
                     download.Season, 
                     download.Episode, 
                     item.Type, 
                     download.Type);
            }
        }
    }

    private async Task FilterTvMetadataAsync(MediaItem series, Dictionary<string, object> metadata)
    {
        // 1. Fetch existing Seasons and Episodes (Lightweight projection)
        // We need to know which Season/Episode numbers exist for this SeriesId
        var existingSeasons = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Season)
            .Select(m => m.SeasonNumber ?? -1)
            .ToListAsync();

        var existingEpisodes = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Episode)
            .Select(m => new { S = m.SeasonNumber ?? 0, E = m.EpisodeNumber ?? 0 })
            .ToListAsync();

        var seasonSet = new HashSet<int>(existingSeasons);
        var episodeSet = new HashSet<(int, int)>(existingEpisodes.Select(x => (x.S, x.E)));

        // 2. Filter Seasons
        if (metadata.TryGetValue("seasons", out var sObj) && sObj is JsonElement sArr)
        {
            var filteredSeasons = new List<Dictionary<string, object?>>();
            foreach (var s in sArr.EnumerateArray())
            {
                var sDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetRawText());
                if (sDict != null && sDict.TryGetValue("number", out var nObj) && int.TryParse(nObj?.ToString(), out var num))
                {
                    if (seasonSet.Contains(num))
                    {
                        filteredSeasons.Add(sDict);
                    }
                }
            }
            metadata["seasons"] = filteredSeasons;
        }
        else if (metadata["seasons"] is List<Dictionary<string, object?>> sList)
        {
             // Already a list (from previous merge step?), filter it
             var filtered = sList.Where(s => 
                s.TryGetValue("number", out var n) && 
                int.TryParse(n?.ToString(), out var num) && 
                seasonSet.Contains(num)).ToList();
             metadata["seasons"] = filtered;
        }

        // 3. Filter Episodes
        if (metadata.TryGetValue("episodes", out var eObj) && eObj is JsonElement eArr)
        {
            var filteredEpisodes = new List<Dictionary<string, object?>>();
            foreach (var e in eArr.EnumerateArray())
            {
                var eDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(e.GetRawText());
                if (eDict != null && 
                    eDict.TryGetValue("season", out var sObj2) && int.TryParse(sObj2?.ToString(), out var sNum) &&
                    eDict.TryGetValue("episode", out var eNumObj) && int.TryParse(eNumObj?.ToString(), out var eNum))
                {
                    if (episodeSet.Contains((sNum, eNum)))
                    {
                        filteredEpisodes.Add(eDict);
                    }
                }
            }
            metadata["episodes"] = filteredEpisodes;
        }
        else if (metadata.TryGetValue("episodes", out var eListObj) && eListObj is List<Dictionary<string, object?>> eList)
        {
             // Already a list?
             var filtered = eList.Where(e => 
                e.TryGetValue("season", out var s) && int.TryParse(s?.ToString(), out var sNum) &&
                e.TryGetValue("episode", out var ep) && int.TryParse(ep?.ToString(), out var epNum) &&
                episodeSet.Contains((sNum, epNum))).ToList();
             metadata["episodes"] = filtered;
        }
    }
}
