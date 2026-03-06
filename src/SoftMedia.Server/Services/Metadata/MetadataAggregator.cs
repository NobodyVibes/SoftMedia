using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;

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
    private readonly IImageUrlExtractorService _imageUrlExtractor;
    private readonly AppDbContext _context;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        IImageUrlExtractorService imageUrlExtractor,
        AppDbContext context,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageUrlExtractor = imageUrlExtractor;
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
                    var existing = MetadataJsonHelper.Parse(item.MetadataJson);
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

            // Promote queryable fields: genres, studio, director
            if (metadata.TryGetValue("genres", out var genresObj) && genresObj != null)
            {
                if (genresObj is JsonElement genresEl && genresEl.ValueKind == JsonValueKind.Array)
                {
                    var genreList = new List<string>();
                    foreach (var g in genresEl.EnumerateArray())
                    {
                        var gStr = g.GetString();
                        if (!string.IsNullOrEmpty(gStr)) genreList.Add(gStr);
                    }
                    if (genreList.Count > 0) item.Genres = string.Join(", ", genreList);
                }
                else
                {
                    var genreStr = genresObj.ToString();
                    if (!string.IsNullOrEmpty(genreStr)) item.Genres = genreStr;
                }
            }
            if (metadata.TryGetValue("studio", out var studioObj) && studioObj != null)
            {
                var studioStr = studioObj.ToString();
                if (!string.IsNullOrEmpty(studioStr)) item.Studio = studioStr;
            }
            if (metadata.TryGetValue("director", out var directorObj) && directorObj != null)
            {
                var directorStr = directorObj.ToString();
                if (!string.IsNullOrEmpty(directorStr)) item.Director = directorStr;
            }

            // FILTERING: Only keep metadata for Seasons/Episodes that exist locally.
            // This MUST run before image extraction and propagation to avoid
            // downloading stills and storing metadata for episodes the user doesn't have.
            if (item.Type == MediaType.Series)
            {
                await FilterTvMetadataAsync(item, metadata);

                // FilterTvMetadataAsync replaces JsonElement arrays with List<Dictionary>,
                // but downstream code expects JsonElement. Re-serialize + re-parse to normalize.
                json = JsonSerializer.Serialize(metadata);
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json) 
                           ?? new Dictionary<string, object>();

                // Propagate episode-level metadata (titles, airdates, stills) to child episodes.
                // Runs after filtering so only local episodes are updated.
                await PropagateEpisodeMetadataAsync(item, metadata);
            }

            // If deferring image caching OR explicitly disabled, skip all image download operations
            // Background service will handle image caching later (if deferred) or never (if disabled)
            if (deferImageCaching || !refreshImages)
            {
                return;
            }

            // Delegate image URL extraction and download queueing to ImageUrlExtractorService
            bool imagesEnqueued = await _imageUrlExtractor.ExtractAndQueueAsync(item, metadata);

            // Update MetadataJson (strip remote URLs)
            item.MetadataJson = JsonSerializer.Serialize(metadata);

            // SAVE to DB to prevent race conditions
            // We save the "clean" metadata first.
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// After series enrichment, pushes episode-level metadata (titles, airdates, stills)
    /// from the TVMaze series response down to child episode MediaItems.
    /// </summary>
    private async Task PropagateEpisodeMetadataAsync(MediaItem series, Dictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("episodes", out var eObj) || eObj is not JsonElement eArr)
            return;

        // Build a lookup: (season, episode) → episode metadata from TVMaze
        var episodeLookup = new Dictionary<(int, int), JsonElement>();
        foreach (var ep in eArr.EnumerateArray())
        {
            if (ep.TryGetProperty("season", out var sProp) && sProp.TryGetInt32(out var s) &&
                ep.TryGetProperty("episode", out var eProp) && eProp.TryGetInt32(out var e))
            {
                episodeLookup[(s, e)] = ep;
            }
        }

        if (episodeLookup.Count == 0) return;

        // Fetch child episodes from DB
        var childEpisodes = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Episode)
            .ToListAsync();

        int updated = 0;
        foreach (var child in childEpisodes)
        {
            var sn = child.SeasonNumber ?? 0;
            var en = child.EpisodeNumber ?? 0;
            if (!episodeLookup.TryGetValue((sn, en), out var epData))
                continue;

            // Title: prefer TVMaze authoritative title over filename-parsed title
            if (epData.TryGetProperty("name", out var nameProp))
            {
                var tvTitle = nameProp.GetString();
                if (!string.IsNullOrEmpty(tvTitle))
                    child.Title = tvTitle;
            }

            // Airdate → ReleaseDate
            if (epData.TryGetProperty("airdate", out var adProp))
            {
                var adStr = adProp.GetString();
                if (DateTime.TryParse(adStr, out var airdate))
                    child.ReleaseDate = airdate;
            }

            // Overview/Summary
            if (epData.TryGetProperty("summary", out var sumProp))
            {
                var summary = sumProp.GetString();
                if (!string.IsNullOrEmpty(summary))
                    child.Overview = summary;
            }

            // Still image → episode MetadataJson
            if (epData.TryGetProperty("still", out var stillProp))
            {
                var stillUrl = stillProp.GetString();
                if (!string.IsNullOrEmpty(stillUrl))
                {
                    var epMeta = MetadataJsonHelper.Parse(child.MetadataJson);
                    epMeta["still"] = stillUrl;
                    child.MetadataJson = JsonSerializer.Serialize(epMeta);
                }
            }

            updated++;
        }

        if (updated > 0)
        {
            _logger.LogInformation("[MetadataAggregator] Propagated metadata to {Count} episodes for '{Series}'", 
                updated, series.Title);
            await _context.SaveChangesAsync();
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
