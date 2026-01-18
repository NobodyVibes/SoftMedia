using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

public class TVMazeProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TVMazeProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public LibraryType SupportedType => LibraryType.TV;
    public string ProviderName => "TVMaze";

    public TVMazeProvider(HttpClient httpClient, ILogger<TVMazeProvider> logger, RateLimiterFactory rateLimiterFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("TVMaze");
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;
        var targetYear = item.Year;
        
        try
        {
            // Acquire rate limit lease before making API call
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning($"TVMaze rate limit exceeded for '{title}', request was queued too long");
                return null;
            }

            // 1. First, check for cached IDs in MetadataJson to skip search
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    using var existingDoc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
                    
                    // Priority A: TVMaze ID (Native)
                    if (existingDoc.RootElement.TryGetProperty("tvmazeId", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var tvmazeId = idProp.GetInt32();
                        _logger.LogInformation($"Using cached TVMaze ID for '{title}': {tvmazeId}");
                        var directUrl = $"https://api.tvmaze.com/shows/{tvmazeId}";
                        try 
                        {
                            var response = await _httpClient.GetStringAsync(directUrl);
                            using var doc = System.Text.Json.JsonDocument.Parse(response);
                            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                return await ProcessShowMetadataAsync(doc.RootElement, title);
                            }
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            _logger.LogWarning($"Cached TVMaze ID {tvmazeId} not found, falling back to search");
                        }
                    } 
                    
                    // Priority B: IMDb ID (Lookup)
                    else if (existingDoc.RootElement.TryGetProperty("imdbId", out var imdbProp))
                    {
                         var imdbId = imdbProp.GetString();
                         if (!string.IsNullOrEmpty(imdbId) && imdbId.StartsWith("tt"))
                         {
                             _logger.LogInformation($"Using cached IMDb ID for '{title}': {imdbId}");
                             var lookupUrl = $"https://api.tvmaze.com/lookup/shows?imdb={imdbId}";
                             try
                             {
                                 var response = await _httpClient.GetStringAsync(lookupUrl);
                                 // TVMaze lookup redirects to the show endpoint, so we get the Show object directly
                                 using var doc = System.Text.Json.JsonDocument.Parse(response);
                                 if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                                 {
                                     return await ProcessShowMetadataAsync(doc.RootElement, title);
                                 }
                             }
                             catch (Exception ex)
                             {
                                  _logger.LogDebug(ex, "Failed lookup by IMDb ID");
                             }
                         }
                    }
                }
                catch (Exception ex)
                {
                     _logger.LogDebug(ex, "Failed to parse existing metadata for ID check");
                }
            }
            
            // Use /search/shows endpoint to get multiple results for year-based disambiguation
            // Per TVMaze API: https://www.tvmaze.com/api#show-search
            var searchUrl = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(title)}";
            _logger.LogInformation($"Fetching TVMaze search for '{title}' (year: {targetYear}): {searchUrl}");
            
            string searchResponse;
            try 
            {
                searchResponse = await _httpClient.GetStringAsync(searchUrl);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning($"TVMaze search returned 404 for '{title}'");
                return null;
            }
            
            using var searchDoc = System.Text.Json.JsonDocument.Parse(searchResponse);
            var searchResults = searchDoc.RootElement;
            
            if (searchResults.GetArrayLength() == 0)
            {
                _logger.LogWarning($"No TVMaze results found for '{title}'");
                return null;
            }
            
            // Find the best matching show - prefer year match if targetYear is provided
            // Priority: 1) Exact year match, 2) Adjacent year (±1), 3) Best relevancy score
            System.Text.Json.JsonElement? bestMatch = null;
            System.Text.Json.JsonElement? adjacentYearMatch = null;
            int bestScore = 0;
            int adjacentYearScore = 0;
            
            foreach (var result in searchResults.EnumerateArray())
            {
                if (!result.TryGetProperty("show", out var show)) continue;
                
                var score = result.TryGetProperty("score", out var scoreVal) ? (int)(scoreVal.GetDouble() * 100) : 0;
                
                // Extract year from premiered date
                int? showYear = null;
                if (show.TryGetProperty("premiered", out var premiered) && premiered.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    var dateStr = premiered.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
                    {
                        showYear = date.Year;
                    }
                }
                
                // If we have a target year, prioritize year matches
                if (targetYear.HasValue && showYear.HasValue)
                {
                    // Priority 1: Exact year match - use this show immediately
                    if (showYear.Value == targetYear.Value)
                    {
                        _logger.LogInformation($"Found exact year match for '{title}' ({targetYear}): {show.GetProperty("name").GetString()}");
                        bestMatch = show;
                        break;
                    }
                    
                    // Priority 2: Adjacent year match (±1) - store best one found
                    if (Math.Abs(showYear.Value - targetYear.Value) == 1)
                    {
                        if (score > adjacentYearScore)
                        {
                            adjacentYearMatch = show;
                            adjacentYearScore = score;
                        }
                    }
                }
                
                // Priority 3: Use highest relevancy score as fallback
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = show;
                }
            }
            
            // Use adjacent year match if no exact match found and adjacent is available
            if (!bestMatch.HasValue || (bestScore < adjacentYearScore + 50 && adjacentYearMatch.HasValue))
            {
                if (adjacentYearMatch.HasValue)
                {
                    _logger.LogInformation($"Using adjacent year match (±1) for '{title}': {adjacentYearMatch.Value.GetProperty("name").GetString()}");
                    bestMatch = adjacentYearMatch;
                }
            }
            
            if (!bestMatch.HasValue)
            {
                _logger.LogWarning($"No suitable TVMaze match found for '{title}'");
                return null;
            }
            
            var root = bestMatch.Value;
            
            return await ProcessShowMetadataAsync(root, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            var errorData = new Dictionary<string, string> { { "error", ex.Message }, { "stack", ex.StackTrace ?? "" } };
            return System.Text.Json.JsonSerializer.Serialize(errorData);
        }
    }

    private async Task<string> ProcessShowMetadataAsync(System.Text.Json.JsonElement root, string title)
    {
            var metadata = new Dictionary<string, object>();
            
            // Store TVMaze show ID for season/episode lookups
            int? tvmazeId = null;
            if (root.TryGetProperty("id", out var idVal))
            {
                tvmazeId = idVal.GetInt32();
                metadata["tvmazeId"] = tvmazeId.Value;
            }
            
            if (root.TryGetProperty("premiered", out var premieredVal))
            {
               var dateStr = premieredVal.GetString();
               if (!string.IsNullOrEmpty(dateStr))
               {
                   metadata["premiered"] = dateStr; // Store raw string for display
                   if (DateTime.TryParse(dateStr, out var date))
                   {
                       metadata["year"] = date.Year;
                       metadata["releaseDate"] = date.ToString("yyyy-MM-dd");
                   }
               }
            }

            if (root.TryGetProperty("status", out var statusVal))
            {
                var status = statusVal.GetString();
                if (!string.IsNullOrEmpty(status)) metadata["status"] = status;
            }

            if (root.TryGetProperty("summary", out var summary))
            {
                // Strip HTML tags
                var summaryText = System.Text.RegularExpressions.Regex.Replace(summary.GetString() ?? "", "<.*?>", "");
                metadata["description"] = summaryText;
            }
            
            if (root.TryGetProperty("rating", out var ratingObj) && ratingObj.TryGetProperty("average", out var avg))
            {
                if (avg.ValueKind != System.Text.Json.JsonValueKind.Null)
                    metadata["rating"] = avg.GetDouble();
            }
            
            if (root.TryGetProperty("genres", out var genresArray))
            {
                metadata["genres"] = genresArray.EnumerateArray().Select(g => g.GetString()).ToList();
            }

            if (root.TryGetProperty("image", out var imageObj) && imageObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (imageObj.TryGetProperty("original", out var original))
                {
                    var poster = original.GetString();
                    if (poster != null) metadata["poster"] = poster;
                }
            }

            if (root.TryGetProperty("network", out var networkObj) && networkObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (networkObj.TryGetProperty("name", out var netName))
                {
                    var studio = netName.GetString();
                    if (studio != null) 
                    {
                        metadata["studio"] = studio;
                        metadata["network"] = studio;
                    }
                }
            }
            else if (root.TryGetProperty("webChannel", out var webChannelObj) && webChannelObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (webChannelObj.TryGetProperty("name", out var webName))
                {
                    var channel = webName.GetString();
                    if (channel != null)
                    {
                        metadata["studio"] = channel;
                        metadata["network"] = channel;
                    }
                }
            }

            if (root.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("cast", out var castArray))
            {
                var castList = new List<object>();
                foreach (var castMember in castArray.EnumerateArray())
                {
                    if (castMember.TryGetProperty("person", out var person) && person.TryGetProperty("name", out var name))
                    {
                        var characterName = "Unknown";
                        string? personImage = null;
                        
                        if (castMember.TryGetProperty("character", out var character) && character.TryGetProperty("name", out var charName))
                        {
                            characterName = charName.GetString();
                        }
                        
                        // Get actor profile image
                        if (person.TryGetProperty("image", out var personImageObj) && personImageObj.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            if (personImageObj.TryGetProperty("medium", out var mediumImg))
                            {
                                personImage = mediumImg.GetString();
                            }
                        }

                        castList.Add(new { name = name.GetString(), character = characterName, image = personImage });
                    }
                }
                metadata["cast"] = castList.Take(10).ToList(); // Top 10
            }

            // Fetch seasons with poster images
            if (tvmazeId.HasValue)
            {
                try
                {
                    var seasonsUrl = $"https://api.tvmaze.com/shows/{tvmazeId}/seasons";
                    var seasonsResponse = await _httpClient.GetStringAsync(seasonsUrl);
                    using var seasonsDoc = System.Text.Json.JsonDocument.Parse(seasonsResponse);
                    
                    var seasonsList = new List<object>();
                    foreach (var season in seasonsDoc.RootElement.EnumerateArray())
                    {
                        var seasonData = new Dictionary<string, object?>();
                        
                        if (season.TryGetProperty("id", out var seasonId))
                            seasonData["id"] = seasonId.GetInt32();
                        if (season.TryGetProperty("number", out var seasonNum) && seasonNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                            seasonData["number"] = seasonNum.GetInt32();
                        if (season.TryGetProperty("episodeOrder", out var epOrder) && epOrder.ValueKind != System.Text.Json.JsonValueKind.Null)
                            seasonData["episodeCount"] = epOrder.GetInt32();
                        if (season.TryGetProperty("premiereDate", out var premDate) && premDate.ValueKind != System.Text.Json.JsonValueKind.Null)
                            seasonData["premiereDate"] = premDate.GetString();
                        if (season.TryGetProperty("image", out var seasonImg) && seasonImg.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            if (seasonImg.TryGetProperty("original", out var seasonOriginal))
                                seasonData["poster"] = seasonOriginal.GetString();
                            else if (seasonImg.TryGetProperty("medium", out var seasonMedium))
                                seasonData["poster"] = seasonMedium.GetString();
                        }
                        
                        seasonsList.Add(seasonData);
                    }
                    
                    // Note: Season 0 (Specials) will be added after we process episodes
                    // to count how many specials exist
                    metadata["seasons"] = seasonsList;
                    _logger.LogInformation($"Fetched {seasonsList.Count} seasons for {title}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to fetch seasons for {title}");
                }

                // Fetch episodes with still images
                try
                {
                    var episodesUrl = $"https://api.tvmaze.com/shows/{tvmazeId}/episodes?specials=1";
                    var episodesResponse = await _httpClient.GetStringAsync(episodesUrl);
                    using var episodesDoc = System.Text.Json.JsonDocument.Parse(episodesResponse);
                    
                    var episodesList = new List<object>();
                    foreach (var episode in episodesDoc.RootElement.EnumerateArray())
                    {
                        var epData = new Dictionary<string, object?>();
                        
                        if (episode.TryGetProperty("id", out var epId))
                            epData["id"] = epId.GetInt32();
                        if (episode.TryGetProperty("number", out var epNum) && epNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            // Regular episode with episode number
                            if (episode.TryGetProperty("season", out var epSeason))
                                epData["season"] = epSeason.GetInt32();
                            epData["episode"] = epNum.GetInt32();
                        }
                        else
                        {
                            // Special episode (number is null) - map to Season 0
                            // This matches the S00E01 naming convention used by media organizers
                            epData["season"] = 0;
                            // Episode number will be sequential among specials, assigned below
                        }
                        if (episode.TryGetProperty("name", out var epName))
                            epData["title"] = epName.GetString();
                        if (episode.TryGetProperty("airdate", out var airdate) && airdate.ValueKind != System.Text.Json.JsonValueKind.Null)
                            epData["airdate"] = airdate.GetString();
                        if (episode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind != System.Text.Json.JsonValueKind.Null)
                            epData["runtime"] = runtime.GetInt32();
                        if (episode.TryGetProperty("summary", out var epSummary) && epSummary.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            var epSummaryText = System.Text.RegularExpressions.Regex.Replace(epSummary.GetString() ?? "", "<.*?>", "");
                            epData["summary"] = epSummaryText;
                        }
                        if (episode.TryGetProperty("image", out var epImg) && epImg.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            if (epImg.TryGetProperty("original", out var epOriginal))
                                epData["still"] = epOriginal.GetString();
                            else if (epImg.TryGetProperty("medium", out var epMedium))
                                epData["still"] = epMedium.GetString();
                        }
                        
                        episodesList.Add(epData);
                    }
                    
                    // Assign sequential episode numbers to Season 0 (specials)
                    // This allows matching S00E01, S00E02, etc. from filenames
                    int specialEpisodeNumber = 1;
                    foreach (var ep in episodesList)
                    {
                        var epDict = ep as Dictionary<string, object?>;
                        if (epDict != null && epDict.TryGetValue("season", out var seasonObj) && 
                            seasonObj is int season && season == 0 && !epDict.ContainsKey("episode"))
                        {
                            epDict["episode"] = specialEpisodeNumber++;
                        }
                    }
                    
                    metadata["episodes"] = episodesList;
                    var specialCount = episodesList.Cast<Dictionary<string, object?>>().Count(e => e.TryGetValue("season", out var s) && s is int si && si == 0);
                    _logger.LogInformation($"Fetched {episodesList.Count} episodes ({specialCount} specials as Season 0) for {title}");
                    
                    // Add Season 0 (Specials) to seasons list if we have any specials
                    if (specialCount > 0 && metadata.TryGetValue("seasons", out var seasonsObj) && seasonsObj is List<object> seasonsList2)
                    {
                        // Create Season 0 entry
                        var season0 = new Dictionary<string, object?>
                        {
                            ["id"] = 0,
                            ["number"] = 0,
                            ["episodeCount"] = specialCount,
                            // Use show poster as fallback for Season 0
                            ["poster"] = metadata.TryGetValue("poster", out var showPoster) ? showPoster : null
                        };
                        
                        // Insert at the beginning of the list
                        seasonsList2.Insert(0, season0);
                        _logger.LogInformation($"Added Season 0 (Specials) with {specialCount} episodes for {title}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to fetch episodes for {title}");
                }
            }

            return System.Text.Json.JsonSerializer.Serialize(metadata);
    }
    }
