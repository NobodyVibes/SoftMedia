using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

public class TVMazeProvider : ISearchableMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TVMazeProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IProviderLookupCache? _lookupCache;

    public LibraryType SupportedType => LibraryType.TV;
    public string ProviderName => "TVMaze";

    public TVMazeProvider(HttpClient httpClient, ILogger<TVMazeProvider> logger, RateLimiterFactory rateLimiterFactory,
        IProviderLookupCache? lookupCache = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("TVMaze");
        _lookupCache = lookupCache;
    }

    /// <summary>
    /// SM-WI-021/022 — the single funnel for every TVMaze HTTP request: exactly ONE
    /// rate-limiter lease per request. The official budget (20 calls / 10 s per IP,
    /// tvmaze.com/api#rate-limiting) counts REQUESTS; the old one-lease-per-lookup
    /// pattern let up to 3 requests ride a single permit (~36/10 s worst case), and the
    /// Fix-Match search path held no lease at all. Throws InvalidOperationException when
    /// the local wait queue is full — genuinely exceptional, handled by callers' catches.
    /// </summary>
    private async Task<string> GetStringLimitedAsync(string url, CancellationToken ct = default)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"TVMaze rate-limit queue is full; request rejected locally: {url}");
        }
        return await _httpClient.GetStringAsync(url, ct);
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
            // 1. First, check for promoted IDs to skip search
            // Priority A: TVMaze ID (Native)
            if (item.TvMazeId.HasValue && item.TvMazeId.Value > 0)
            {
                var tvmazeId = item.TvMazeId.Value;
                _logger.LogInformation($"Using promoted TVMaze ID for '{title}': {tvmazeId}");
                var directUrl = $"https://api.tvmaze.com/shows/{tvmazeId}?embed[]=cast&embed[]=seasons&embed[]=episodes";
                try 
                {
                    var response = await GetStringLimitedAsync(directUrl);
                    using var doc = System.Text.Json.JsonDocument.Parse(response);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        return ProcessShowMetadata(doc.RootElement, title, response);
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning($"Promoted TVMaze ID {tvmazeId} not found, falling back to search");
                }
            } 
            // Priority B: IMDb ID (Lookup)
            else if (!string.IsNullOrEmpty(item.ImdbId) && item.ImdbId.StartsWith("tt"))
            {
                _logger.LogInformation($"Using promoted IMDb ID for '{title}': {item.ImdbId}");
                var lookupUrl = $"https://api.tvmaze.com/lookup/shows?imdb={item.ImdbId}";
                try
                {
                    var response = await GetStringLimitedAsync(lookupUrl);
                    using var doc = System.Text.Json.JsonDocument.Parse(response);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Null && doc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        var resolvedId = idEl.GetInt32();
                        var fullDetailUrl = $"https://api.tvmaze.com/shows/{resolvedId}?embed[]=cast&embed[]=seasons&embed[]=episodes";
                        var fullResponse = await GetStringLimitedAsync(fullDetailUrl);
                        using var fullDoc = System.Text.Json.JsonDocument.Parse(fullResponse);
                        return ProcessShowMetadata(fullDoc.RootElement, title, fullResponse);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed lookup by IMDb ID");
                }
            }
            
            // SM-WI-040: the ID paths above are authoritative; only this title-search
            // fallback is cacheable. A fresh cached miss skips the network entirely.
            var cacheKey = ProviderLookupCacheService.NormalizeKey(item.Type, title, targetYear);
            if (_lookupCache != null && await _lookupCache.IsFreshMissAsync(ProviderName, cacheKey))
            {
                _logger.LogDebug("TVMaze: fresh cached miss for '{Title}'; skipping search", title);
                return null;
            }

            // Use /search/shows endpoint to get multiple results for year-based disambiguation
            // Per TVMaze API: https://www.tvmaze.com/api#show-search
            var searchUrl = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(title)}";
            _logger.LogInformation($"Fetching TVMaze search for '{title}' (year: {targetYear}): {searchUrl}");

            string searchResponse;
            try
            {
                searchResponse = await GetStringLimitedAsync(searchUrl);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning($"TVMaze search returned 404 for '{title}'");
                if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
                return null;
            }

            using var searchDoc = System.Text.Json.JsonDocument.Parse(searchResponse);
            var searchResults = searchDoc.RootElement;

            if (searchResults.GetArrayLength() == 0)
            {
                _logger.LogWarning($"No TVMaze results found for '{title}'");
                if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
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
                if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
                return null;
            }
            
            var matchId = bestMatch.Value.GetProperty("id").GetInt32();
            
            // Fetch full details with cast, seasons, and episodes in a single request
            // Per TVMaze docs: ?embed[]=cast&embed[]=seasons&embed[]=episodes
            var detailUrl = $"https://api.tvmaze.com/shows/{matchId}?embed[]=cast&embed[]=seasons&embed[]=episodes";
            _logger.LogInformation("Fetching full details for match ID {MatchId}: {Url}", matchId, detailUrl);

            var detailResponse = await GetStringLimitedAsync(detailUrl);
            using var detailDoc = System.Text.Json.JsonDocument.Parse(detailResponse);
            
            return ProcessShowMetadata(detailDoc.RootElement, title, detailResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            return null; // Don't return error JSON
        }
    }

    /// <summary>
    /// Parses the full show response (with ?embed[]=cast&embed[]=seasons&embed[]=episodes)
    /// into a MetadataResult. Eliminates the need for separate API calls for seasons and episodes.
    /// </summary>
    private MetadataResult ProcessShowMetadata(System.Text.Json.JsonElement root, string title, string rawPayload)
    {
            var result = new MetadataResult();
            result.RawPayload = rawPayload;
            result.SourceProvider = ProviderName; // SM-WI-045: labels the payload cache row
            
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
                // SM-WI-044: typed field (the old Extra["status"] was persisted only for
                // photos — i.e. computed then dropped). Drives the "Running" refresh mode.
                var status = statusVal.GetString();
                if (!string.IsNullOrEmpty(status))
                {
                    result.SeriesStatus = status;
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

            // Parse embedded data (cast, seasons, episodes) — all from a single API call
            if (root.TryGetProperty("_embedded", out var embedded))
            {
                // --- Cast ---
                if (embedded.TryGetProperty("cast", out var castArray))
                {
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

                // --- Seasons ---
                if (embedded.TryGetProperty("seasons", out var seasonsArray))
                {
                    result.Seasons = new List<SeasonMetadata>();
                    foreach (var season in seasonsArray.EnumerateArray())
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
                    _logger.LogInformation("Fetched {Count} seasons for {Title}", result.Seasons.Count, title);
                }

                // --- Episodes ---
                if (embedded.TryGetProperty("episodes", out var episodesArray))
                {
                    result.Episodes = new List<EpisodeMetadata>();
                    foreach (var episode in episodesArray.EnumerateArray())
                    {
                        var epData = new EpisodeMetadata();
                        
                        if (episode.TryGetProperty("id", out var epId))
                            epData.Id = epId.GetInt32();
                            
                        if (episode.TryGetProperty("number", out var epNum) && epNum.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
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
                    _logger.LogInformation("Fetched {EpCount} episodes ({SpecialCount} specials as Season 0) for {Title}", result.Episodes.Count, specialCount, title);
                    
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
                        _logger.LogInformation("Added Season 0 (Specials) with {Count} episodes for {Title}", specialCount, title);
                    }
                }
            }

            return result;
    }

    // --- ISearchableMetadataProvider (P3-WI-003 Fix Match) ---

    /// <summary>
    /// Free-text search for the "Fix Match" admin flow. Reuses the same /search/shows
    /// endpoint that <see cref="FetchMetadataAsync"/> calls internally for year
    /// disambiguation, but returns up to 10 ranked candidates (TVMaze's own relevancy
    /// order) so the admin can pick the right show by poster/year rather than relying
    /// on automatic disambiguation.
    /// </summary>
    public async Task<IReadOnlyList<MetadataSearchCandidate>> SearchAsync(string query, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MetadataSearchCandidate>();

        var searchUrl = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query.Trim())}";
        string body;
        try { body = await GetStringLimitedAsync(searchUrl, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVMaze search failed for '{Query}'", query);
            return Array.Empty<MetadataSearchCandidate>();
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var candidates = new List<MetadataSearchCandidate>();

        foreach (var result in doc.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("show", out var show)) continue;
            var id = show.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.Number
                ? idEl.GetInt32().ToString()
                : null;
            if (string.IsNullOrEmpty(id)) continue;

            int? premieredYear = null;
            if (show.TryGetProperty("premiered", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTime.TryParse(p.GetString(), out var d)) premieredYear = d.Year;

            // Optional client-side year filter (caller passes year? from the parsed filename).
            if (year.HasValue && premieredYear.HasValue && Math.Abs(premieredYear.Value - year.Value) > 1)
                continue;

            string? poster = null;
            if (show.TryGetProperty("image", out var img) && img.ValueKind == System.Text.Json.JsonValueKind.Object
                && img.TryGetProperty("medium", out var medium) && medium.ValueKind == System.Text.Json.JsonValueKind.String)
                poster = medium.GetString();

            string? network = null;
            if (show.TryGetProperty("network", out var net) && net.ValueKind == System.Text.Json.JsonValueKind.Object
                && net.TryGetProperty("name", out var netName) && netName.ValueKind == System.Text.Json.JsonValueKind.String)
                network = netName.GetString();

            candidates.Add(new MetadataSearchCandidate(
                ProviderName,
                id,
                show.GetProperty("name").GetString() ?? "(untitled)",
                premieredYear,
                poster,
                network));

            if (candidates.Count >= 10) break;
        }
        return candidates;
    }

    /// <summary>
    /// Fetches full metadata for a candidate the admin picked from <see cref="SearchAsync"/>.
    /// Synthesises a transient MediaItem with the chosen TVMaze id so the existing
    /// FetchMetadataAsync short-circuit (line 48: "promoted TvMazeId") handles the actual call.
    /// </summary>
    public Task<MetadataResult?> FetchByCandidateAsync(string providerItemId, CancellationToken ct)
    {
        if (!int.TryParse(providerItemId, out var tvMazeId) || tvMazeId <= 0)
            return Task.FromResult<MetadataResult?>(null);

        // Reuse FetchMetadataAsync; the TvMazeId short-circuit handles the rest.
        return FetchMetadataAsync(new MediaItem
        {
            Title = "(fix-match)",
            Type = Models.MediaType.Series,
            TvMazeId = tvMazeId,
        });
    }
}
