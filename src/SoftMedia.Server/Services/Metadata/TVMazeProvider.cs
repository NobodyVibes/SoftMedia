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

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Episodes get their metadata from the parent series fetch — never search individually
        if (item.Type == Models.MediaType.Episode || item.Type == Models.MediaType.Season)
        {
            _logger.LogDebug("Skipping TVMaze search for {Type} '{Title}' — metadata comes from series", item.Type, item.Title);
            return null;
        }

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
                        var directUrl = $"https://api.tvmaze.com/shows/{tvmazeId}?embed=cast";
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
                             // Add embed=cast to lookup (TVMaze redirects, but usually preserves params or we can get ID and refetch)
                             var lookupUrl = $"https://api.tvmaze.com/lookup/shows?imdb={imdbId}";
                             try
                             {
                                 var response = await _httpClient.GetStringAsync(lookupUrl);
                                 // The lookup returns the show object directly (following redirect)
                                 // However, the redirect might drop the 'embed' param if not handled by API.
                                 // Safe bet: Parse ID from response and refetch with embed if _embedded is missing, 
                                 // OR just assume lookup returns basic info and we need to refetch.
                                 // Better: Parse response, get ID, then fetch full info with embed.
                                 using var doc = System.Text.Json.JsonDocument.Parse(response);
                                 if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null && doc.RootElement.TryGetProperty("id", out var idEl))
                                 {
                                     var resolvedId = idEl.GetInt32();
                                     var fullDetailUrl = $"https://api.tvmaze.com/shows/{resolvedId}?embed=cast";
                                     var fullResponse = await _httpClient.GetStringAsync(fullDetailUrl);
                                     using var fullDoc = System.Text.Json.JsonDocument.Parse(fullResponse);
                                     return await ProcessShowMetadataAsync(fullDoc.RootElement, title);
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
            
            // If parsed year is 0 (often from poorly parsed filenames), ignore it for matching
            if (targetYear <= 0) targetYear = null;

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
            
            var matchId = bestMatch.Value.GetProperty("id").GetInt32();
            
            // Fetch full details with cast
            var detailUrl = $"https://api.tvmaze.com/shows/{matchId}?embed=cast";
            _logger.LogInformation($"Fetching full details for match ID {matchId}: {detailUrl}");
            
            // Acquire rate limit lease before making API call
            using var lease4 = await _rateLimiter.AcquireAsync(1);
            if (!lease4.IsAcquired)
            {
                _logger.LogWarning($"TVMaze rate limit exceeded for '{title}', request was queued too long");
                return null;
            }

            var detailResponse = await _httpClient.GetStringAsync(detailUrl);
            using var detailDoc = System.Text.Json.JsonDocument.Parse(detailResponse);
            
            return await ProcessShowMetadataAsync(detailDoc.RootElement, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            return null; // Don't return error JSON
        }
    }

    private async Task<MetadataResult> ProcessShowMetadataAsync(System.Text.Json.JsonElement root, string title)
    {
            var result = new MetadataResult();
            
            // Store TVMaze show ID for season/episode lookups
            if (root.TryGetProperty("id", out var idVal))
            {
                result.TvMazeId = idVal.GetInt32();
            }
            
            if (root.TryGetProperty("premiered", out var premieredVal))
            {
               var dateStr = premieredVal.GetString();
               if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
               {
                   result.Year = date.Year;
                   result.ReleaseDate = date;
               }
            }

            if (root.TryGetProperty("status", out var statusVal))
            {
                var status = statusVal.GetString();
                if (!string.IsNullOrEmpty(status)) 
                {
                    result.Extra ??= new Dictionary<string, System.Text.Json.JsonElement>();
                    result.Extra["status"] = System.Text.Json.JsonSerializer.SerializeToElement(status);
                }
            }

            if (root.TryGetProperty("summary", out var summary))
            {
                // Strip HTML tags
                var summaryText = System.Text.RegularExpressions.Regex.Replace(summary.GetString() ?? "", "<.*?>", "");
                result.Description = summaryText;
            }
            
            if (root.TryGetProperty("rating", out var ratingObj) && ratingObj.TryGetProperty("average", out var avg))
            {
                if (avg.ValueKind != System.Text.Json.JsonValueKind.Null)
                    result.Rating = avg.GetDouble();
            }
            
            if (root.TryGetProperty("genres", out var genresArray))
            {
                result.Genres = genresArray.EnumerateArray().Select(g => g.GetString()!).ToList();
            }

            if (root.TryGetProperty("image", out var imageObj) && imageObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (imageObj.TryGetProperty("original", out var original))
                {
                    var poster = original.GetString();
                    if (poster != null) result.PosterUrl = poster;
                }
            }

            if (root.TryGetProperty("network", out var networkObj) && networkObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (networkObj.TryGetProperty("name", out var netName))
                {
                    var studio = netName.GetString();
                    if (studio != null) result.Studio = studio;
                }
            }
            else if (root.TryGetProperty("webChannel", out var webChannelObj) && webChannelObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (webChannelObj.TryGetProperty("name", out var webName))
                {
                    var channel = webName.GetString();
                    if (channel != null) result.Studio = channel;
                }
            }

            if (root.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("cast", out var castArray))
            {
                // Smart cast deduplication: merge genuinely distinct characters per actor,
                // but filter out obvious variants of the same character.
                // TVMaze orders cast by total appearances (most important first).
                // Examples:
                //   "Leela", "Young Leela", "Leela 1" → "Leela" (variants dropped)
                //   "Fry", "Professor Farnsworth"      → "Fry / Professor Farnsworth" (distinct kept)
                var castLookup = new Dictionary<int, (string Name, List<string> Characters, string? Image)>();
                var castOrder = new List<int>();

                foreach (var castMember in castArray.EnumerateArray())
                {
                    if (!castMember.TryGetProperty("person", out var person) || !person.TryGetProperty("name", out var name))
                        continue;

                    int personId = -1;
                    if (person.TryGetProperty("id", out var idProp))
                    {
                        personId = idProp.GetInt32();
                    }

                    var characterName = "Unknown";
                    if (castMember.TryGetProperty("character", out var character) && character.TryGetProperty("name", out var charName))
                    {
                        characterName = charName.GetString() ?? "Unknown";
                    }

                    if (castLookup.TryGetValue(personId, out var existing))
                    {
                        // Only add if this is a genuinely distinct character (not a variant).
                        // A variant is when the new name contains an existing name or vice versa.
                        // e.g. "Young Leela" contains "Leela" → variant, skip it.
                        var isVariant = existing.Characters.Any(c =>
                            characterName.Contains(c, StringComparison.OrdinalIgnoreCase) ||
                            c.Contains(characterName, StringComparison.OrdinalIgnoreCase));

                        if (!isVariant)
                        {
                            existing.Characters.Add(characterName);
                        }
                    }
                    else
                    {
                        string? personImage = null;
                        if (person.TryGetProperty("image", out var personImageObj) && personImageObj.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            if (personImageObj.TryGetProperty("medium", out var mediumImg))
                            {
                                personImage = mediumImg.GetString();
                            }
                        }

                        castLookup[personId] = (name.GetString() ?? "Unknown", new List<string> { characterName }, personImage);
                        castOrder.Add(personId);
                    }
                }

                result.Cast = new List<CastMember>();
                foreach (var pid in castOrder.Take(10))
                {
                    var c = castLookup[pid];
                    result.Cast.Add(new CastMember 
                    { 
                        Id = pid >= 0 ? pid : null, 
                        Name = c.Name, 
                        Character = string.Join(" / ", c.Characters), 
                        ImageUrl = c.Image 
                    });
                }
            }

            // Fetch seasons with poster images
            if (result.TvMazeId.HasValue)
            {
                try
                {
                    // Acquire rate limit lease before making API call
                    using var lease = await _rateLimiter.AcquireAsync(1);
                    if (!lease.IsAcquired)
                    {
                        _logger.LogWarning($"TVMaze rate limit exceeded for '{title}', request was queued too long");
                        // Continue without seasons, don't return null for entire metadata
                    }
                    else
                    {
                        var seasonsUrl = $"https://api.tvmaze.com/shows/{result.TvMazeId}/seasons";
                        var seasonsResponse = await _httpClient.GetStringAsync(seasonsUrl);
                        using var seasonsDoc = System.Text.Json.JsonDocument.Parse(seasonsResponse);
                        
                        result.Seasons = new List<SeasonMetadata>();
                        foreach (var season in seasonsDoc.RootElement.EnumerateArray())
                        {
                            var seasonData = new SeasonMetadata();
                            
                            if (season.TryGetProperty("id", out var seasonId))
                                seasonData.Id = seasonId.GetInt32();
                            if (season.TryGetProperty("number", out var seasonNum) && seasonNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                                seasonData.Number = seasonNum.GetInt32();
                            if (season.TryGetProperty("premiereDate", out var premDate) && premDate.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                if (DateTime.TryParse(premDate.GetString(), out var sd)) seasonData.PremiereDate = sd;
                            }
                            if (season.TryGetProperty("endDate", out var endDate) && endDate.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                if (DateTime.TryParse(endDate.GetString(), out var ed)) seasonData.EndDate = ed;
                            }
                            if (season.TryGetProperty("image", out var seasonImg) && seasonImg.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                if (seasonImg.TryGetProperty("original", out var seasonOriginal))
                                    seasonData.PosterUrl = seasonOriginal.GetString();
                                else if (seasonImg.TryGetProperty("medium", out var seasonMedium))
                                    seasonData.PosterUrl = seasonMedium.GetString();
                            }
                            
                            result.Seasons.Add(seasonData);
                        }
                        
                        _logger.LogInformation($"Fetched {result.Seasons.Count} seasons for {title}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to fetch seasons for {title}");
                }

                // Fetch episodes with still images
                try
                {
                    // Acquire rate limit lease before making API call
                    using var lease = await _rateLimiter.AcquireAsync(1);
                    if (!lease.IsAcquired)
                    {
                        _logger.LogWarning($"TVMaze rate limit exceeded for '{title}', request was queued too long");
                        // Continue without episodes, don't return null for entire metadata
                    }
                    else
                    {
                        var episodesUrl = $"https://api.tvmaze.com/shows/{result.TvMazeId}/episodes?specials=1";
                        var episodesResponse = await _httpClient.GetStringAsync(episodesUrl);
                        using var episodesDoc = System.Text.Json.JsonDocument.Parse(episodesResponse);
                        
                        result.Episodes = new List<EpisodeMetadata>();
                        foreach (var episode in episodesDoc.RootElement.EnumerateArray())
                        {
                            var epData = new EpisodeMetadata();
                            
                            if (episode.TryGetProperty("id", out var epId))
                                epData.Id = epId.GetInt32();
                                
                            if (episode.TryGetProperty("number", out var epNum) && epNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                // Regular episode with episode number
                                if (episode.TryGetProperty("season", out var epSeason))
                                    epData.SeasonNumber = epSeason.GetInt32();
                                epData.EpisodeNumber = epNum.GetInt32();
                            }
                            else
                            {
                                epData.SeasonNumber = 0;
                            }
                            
                            if (episode.TryGetProperty("name", out var epName))
                                epData.Name = epName.GetString();
                                
                            if (episode.TryGetProperty("airdate", out var airdate) && airdate.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                if (DateTime.TryParse(airdate.GetString(), out var ad)) epData.AirDate = ad;
                            }
                                
                            if (episode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind != System.Text.Json.JsonValueKind.Null)
                                epData.Runtime = runtime.GetInt32();
                                
                            if (episode.TryGetProperty("summary", out var epSummary) && epSummary.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                var epSummaryText = System.Text.RegularExpressions.Regex.Replace(epSummary.GetString() ?? "", "<.*?>", "");
                                epData.Summary = epSummaryText;
                            }
                            
                            if (episode.TryGetProperty("rating", out var epRatingObj) && epRatingObj.TryGetProperty("average", out var epRating) && epRating.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                 epData.Rating = epRating.GetDouble();
                            }
                            
                            if (episode.TryGetProperty("image", out var epImg) && epImg.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                if (epImg.TryGetProperty("original", out var epOriginal))
                                    epData.StillUrl = epOriginal.GetString();
                                else if (epImg.TryGetProperty("medium", out var epMedium))
                                    epData.StillUrl = epMedium.GetString();
                            }
                            
                            result.Episodes.Add(epData);
                        }
                        
                        // Assign sequential episode numbers to Season 0 (specials)
                        int specialEpisodeNumber = 1;
                        foreach (var ep in result.Episodes.Where(e => e.SeasonNumber == 0 && e.EpisodeNumber == 0))
                        {
                            ep.EpisodeNumber = specialEpisodeNumber++;
                        }
                        
                        var specialCount = result.Episodes.Count(e => e.SeasonNumber == 0);
                        _logger.LogInformation($"Fetched {result.Episodes.Count} episodes ({specialCount} specials as Season 0) for {title}");
                        
                        // Add Season 0 (Specials) to seasons list if we have any specials
                        if (specialCount > 0 && result.Seasons != null)
                        {
                            var season0 = new SeasonMetadata
                            {
                                Id = 0,
                                Number = 0,
                                PosterUrl = result.PosterUrl // Fallback
                            };
                            result.Seasons.Insert(0, season0);
                            _logger.LogInformation($"Added Season 0 (Specials) with {specialCount} episodes for {title}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to fetch episodes for {title}");
                }
            }

            return result;
    }
}
