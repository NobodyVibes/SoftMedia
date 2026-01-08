using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public class TVMazeProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TVMazeProvider> _logger;

    public LibraryType SupportedType => LibraryType.TV;
    public string ProviderName => "TVMaze";

    public TVMazeProvider(HttpClient httpClient, ILogger<TVMazeProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;
        var targetYear = item.Year;
        
        try
        {
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
            System.Text.Json.JsonElement? bestMatch = null;
            int bestScore = 0;
            
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
                
                // If we have a target year, prioritize exact year matches
                if (targetYear.HasValue && showYear.HasValue)
                {
                    if (showYear.Value == targetYear.Value)
                    {
                        // Exact year match - use this show
                        _logger.LogInformation($"Found exact year match for '{title}' ({targetYear}): {show.GetProperty("name").GetString()}");
                        bestMatch = show;
                        break;
                    }
                }
                
                // Use highest relevancy score as fallback
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = show;
                }
            }
            
            if (!bestMatch.HasValue)
            {
                _logger.LogWarning($"No suitable TVMaze match found for '{title}'");
                return null;
            }
            
            var root = bestMatch.Value;
            
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
                    var episodesUrl = $"https://api.tvmaze.com/shows/{tvmazeId}/episodes";
                    var episodesResponse = await _httpClient.GetStringAsync(episodesUrl);
                    using var episodesDoc = System.Text.Json.JsonDocument.Parse(episodesResponse);
                    
                    var episodesList = new List<object>();
                    foreach (var episode in episodesDoc.RootElement.EnumerateArray())
                    {
                        var epData = new Dictionary<string, object?>();
                        
                        if (episode.TryGetProperty("id", out var epId))
                            epData["id"] = epId.GetInt32();
                        if (episode.TryGetProperty("season", out var epSeason))
                            epData["season"] = epSeason.GetInt32();
                        if (episode.TryGetProperty("number", out var epNum) && epNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                            epData["episode"] = epNum.GetInt32();
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
                    metadata["episodes"] = episodesList;
                    _logger.LogInformation($"Fetched {episodesList.Count} episodes for {title}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to fetch episodes for {title}");
                }
            }

            return System.Text.Json.JsonSerializer.Serialize(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            var errorData = new Dictionary<string, string> { { "error", ex.Message }, { "stack", ex.StackTrace ?? "" } };
            return System.Text.Json.JsonSerializer.Serialize(errorData);
        }
    }
}
