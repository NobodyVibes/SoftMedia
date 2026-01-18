using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// OMDB API provider for movie metadata.
/// Requires an API key - either the bundled SoftMedia key or a user-provided custom key.
/// Implements daily usage tracking with tier-based limits.
/// </summary>
public class OMDbProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OMDbProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IConfiguration _configuration;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;

    public LibraryType SupportedType => LibraryType.Movie;
    public string ProviderName => "OMDb";

    // Placeholder for SoftMedia bundled key - see docs/OMDB_API_KEY_SETUP.md
    private const string SOFTMEDIA_KEY_PLACEHOLDER = "SOFTMEDIA_OMDB_KEY_PLACEHOLDER";

    // Daily limits by tier
    private static readonly Dictionary<string, int> TierLimits = new()
    {
        { "free", 1_000 },
        { "basic", 100_000 },
        { "standard", 250_000 },
        { "pro", int.MaxValue }
    };

    public OMDbProvider(
        HttpClient httpClient, 
        ILogger<OMDbProvider> logger, 
        RateLimiterFactory rateLimiterFactory,
        IConfiguration configuration,
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("OMDb");
        _configuration = configuration;
        _settingsService = settingsService;
        _notificationService = notificationService;
        
        // Set User-Agent for API compliance
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
        }
    }

    /// <summary>
    /// Gets the active API key based on the configured mode.
    /// Returns null if disabled or no valid key is configured.
    /// </summary>
    public string? GetActiveApiKey(string mode, string? customKey)
    {
        return mode switch
        {
            "softmedia" => GetSoftMediaKey(),
            "custom" => string.IsNullOrWhiteSpace(customKey) ? null : customKey,
            "disabled" => null,
            _ => GetSoftMediaKey() // Default to SoftMedia key
        };
    }

    /// <summary>
    /// Gets the bundled SoftMedia API key from configuration.
    /// </summary>
    private string? GetSoftMediaKey()
    {
        var key = _configuration["OMDb:SoftMediaApiKey"];
        
        // Check if the placeholder is still in use
        if (string.IsNullOrWhiteSpace(key) || key == SOFTMEDIA_KEY_PLACEHOLDER)
        {
            _logger.LogWarning("SoftMedia OMDB API key is not configured. See docs/OMDB_API_KEY_SETUP.md");
            return null;
        }
        
        return key;
    }

    /// <summary>
    /// Checks if daily limit allows another request. Resets counter at midnight UTC.
    /// Only applies when using custom API key.
    /// </summary>
    private async Task<bool> CanMakeRequestAsync(string mode)
    {
        // No tracking for SoftMedia bundled key
        if (mode != "custom")
            return true;

        var tier = await _settingsService.GetSettingAsync("OMDbApiTier", "free");
        var limit = TierLimits.GetValueOrDefault(tier, 1_000);
        
        // Get current count and date
        var countStr = await _settingsService.GetSettingAsync("OMDbDailyCount", "0");
        var dateStr = await _settingsService.GetSettingAsync("OMDbCountDate", "");
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

        int count = 0;
        int.TryParse(countStr, out count);

        // Reset if new day
        if (dateStr != todayStr)
        {
            count = 0;
            await UpdateCountAsync(0, todayStr);
        }

        return count < limit;
    }

    /// <summary>
    /// Records an API request to the daily counter.
    /// </summary>
    private async Task RecordRequestAsync()
    {
        var countStr = await _settingsService.GetSettingAsync("OMDbDailyCount", "0");
        int.TryParse(countStr, out var count);
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        
        await UpdateCountAsync(count + 1, todayStr);
    }

    private async Task UpdateCountAsync(int count, string date)
    {
        await _settingsService.UpdateSettingsAsync(new List<Models.AppSetting>
        {
            new() { Key = "OMDbDailyCount", Value = count.ToString(), Group = "Internal" },
            new() { Key = "OMDbCountDate", Value = date, Group = "Internal" }
        });
    }

    /// <summary>
    /// Creates an exhaustion notification if one doesn't already exist.
    /// </summary>
    private async Task CreateExhaustionNotificationAsync()
    {
        if (!await _notificationService.HasActiveOfTypeAsync("omdb_exhausted"))
        {
            var tier = await _settingsService.GetSettingAsync("OMDbApiTier", "free");
            var limit = TierLimits.GetValueOrDefault(tier, 1_000);
            
            await _notificationService.CreateAsync(
                "omdb_exhausted",
                "OMDb API Limit Reached",
                $"Daily limit ({limit:N0}) exhausted. Movie metadata will be skipped until midnight UTC.",
                "warning"
            );
        }
    }

    public Task<string?> FetchMetadataAsync(MediaItem item)
    {
        // This method is called by MetadataRouter which should pass the resolved API key
        // For direct calls, we return null as the key context isn't available
        _logger.LogDebug("FetchMetadataAsync called directly - use FetchMetadataWithKeyAsync instead");
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Fetches metadata using the provided API key.
    /// </summary>
    public async Task<string?> FetchMetadataWithKeyAsync(MediaItem item, string apiKey, string mode = "custom")
    {
        var title = item.Title;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OMDB API key is not configured for movie: {Title}", title);
            return null;
        }

        // Check daily limit (only for custom key mode)
        if (!await CanMakeRequestAsync(mode))
        {
            _logger.LogWarning("OMDb daily limit exhausted. Skipping metadata for: {Title}", title);
            await CreateExhaustionNotificationAsync();
            return null;
        }

        try
        {
            // Acquire rate limit lease
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("OMDB rate limit exceeded for '{Title}'", title);
                return null;
            }

            // 1. First, check if we already have an IMDb ID cached in MetadataJson
            // This allows us to skip search/fuzzy matching on refreshes and use a single direct API call.
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    using var existingDoc = JsonDocument.Parse(item.MetadataJson);
                    if (existingDoc.RootElement.TryGetProperty("imdbId", out var idProp))
                    {
                        var existingId = idProp.GetString();
                        if (!string.IsNullOrEmpty(existingId) && existingId.StartsWith("tt"))
                        {
                            _logger.LogInformation("Using cached IMDb ID for '{Title}': {Id}", title, existingId);
                            
                            // Direct ID Fetch (Cost: 1 request)
                            var directUrl = $"http://www.omdbapi.com/?apikey={apiKey}&i={existingId}&plot=full";
                            var directResponse = await _httpClient.GetStringAsync(directUrl);
                            
                            if (mode == "custom") await RecordRequestAsync();

                            using var directDoc = JsonDocument.Parse(directResponse);
                             // Check for valid response
                            if (directDoc.RootElement.TryGetProperty("Response", out var resp) && resp.GetString() == "True")
                            {
                                // Return valid result immediately
                                return ProcessProcessAndSerialize(directDoc.RootElement, title);
                            }
                            
                             _logger.LogWarning("Cached IMDb ID {Id} failed lookup, falling back to title search", existingId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse existing metadata for ID check");
                }
            }

            // 2. Fallback: Search by Title + Year
            // Extract year from title if present (e.g., "Movie Name (2023)")
            // Prefer MediaItem.Year if it was set during scanning, else try regex on title
            string? year = null;
            var cleanTitle = title;
            
            // Use year from MediaItem if available (set during filename parsing)
            if (item.Year > 0)
            {
                year = item.Year.ToString();
            }
            
            // Also try to extract from title in case year is embedded there
            var yearMatch = System.Text.RegularExpressions.Regex.Match(title, @"\((\d{4})\)$");
            if (yearMatch.Success)
            {
                if (year == null) year = yearMatch.Groups[1].Value;
                cleanTitle = title.Substring(0, yearMatch.Index).Trim();
            }

            // Build search URL with full plot
            var searchUrl = $"http://www.omdbapi.com/?apikey={apiKey}&t={Uri.EscapeDataString(cleanTitle)}&type=movie&plot=full";
            if (year != null)
            {
                searchUrl += $"&y={year}";
            }

            _logger.LogInformation("Fetching OMDB data for: {Title}", title);
            var response = await _httpClient.GetStringAsync(searchUrl);
            
            // Record successful request (only for custom key)
            if (mode == "custom")
            {
                await RecordRequestAsync();
            }
            
            JsonElement movieData;
            using (var doc = JsonDocument.Parse(response))
            {
                var root = doc.RootElement;

                // Check for API error or no results - try fallback search
                bool exactMatchFailed = root.TryGetProperty("Error", out _) || 
                    !root.TryGetProperty("Response", out var responseProp) || 
                    responseProp.GetString() != "True";

                if (!exactMatchFailed)
                {
                    // Exact match succeeded - clone the data
                    movieData = root.Clone();
                }
                else
                {
                    _logger.LogDebug("Exact match failed for '{Title}', trying search...", title);
                    
                    // Fallback: Use search API (&s=) instead of exact match (&t=)
                    var searchApiUrl = $"http://www.omdbapi.com/?apikey={apiKey}&s={Uri.EscapeDataString(cleanTitle)}&type=movie";
                    if (year != null)
                    {
                        searchApiUrl += $"&y={year}";
                    }

                    var searchResponse = await _httpClient.GetStringAsync(searchApiUrl);
                    
                    // Count this as another request
                    if (mode == "custom")
                    {
                        await RecordRequestAsync();
                    }

                    using var searchDoc = JsonDocument.Parse(searchResponse);
                    var searchRoot = searchDoc.RootElement;

                    if (searchRoot.TryGetProperty("Search", out var searchResults) && 
                        searchResults.GetArrayLength() > 0)
                    {
                        // Get IMDb ID of first result and fetch full details
                        var firstResult = searchResults[0];
                        if (firstResult.TryGetProperty("imdbID", out var searchImdbIdProp))
                        {
                            var imdbId = searchImdbIdProp.GetString();
                            _logger.LogInformation("Found via search: '{Title}' -> {ImdbId}", title, imdbId);
                            
                            // Fetch full details by IMDb ID
                            var detailUrl = $"http://www.omdbapi.com/?apikey={apiKey}&i={imdbId}&plot=full";
                            var detailResponse = await _httpClient.GetStringAsync(detailUrl);
                            
                            if (mode == "custom")
                            {
                                await RecordRequestAsync();
                            }

                            using var detailDoc = JsonDocument.Parse(detailResponse);
                            movieData = detailDoc.RootElement.Clone();
                        }
                        else
                        {
                            _logger.LogDebug("No OMDB results for '{Title}'", title);
                            return null;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No OMDB results for '{Title}'", title);
                        return null;
                    }
                }
            }

            // Build metadata dictionary using the cloned movieData
            return ProcessProcessAndSerialize(movieData, title);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching OMDB data for '{Title}'", title);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching OMDB data for '{Title}'", title);
            return null;
        }
    }

    private string ProcessProcessAndSerialize(JsonElement movieData, string title)
    {
            var metadata = new Dictionary<string, object>();

            if (movieData.TryGetProperty("Year", out var yearProp))
            {
                var yearStr = yearProp.GetString();
                if (!string.IsNullOrEmpty(yearStr) && yearStr != "N/A")
                    metadata["year"] = yearStr;
            }

            if (movieData.TryGetProperty("Plot", out var plotProp))
            {
                var plot = plotProp.GetString();
                if (!string.IsNullOrEmpty(plot) && plot != "N/A")
                    metadata["description"] = plot;
            }

            if (movieData.TryGetProperty("Director", out var dirProp))
            {
                var director = dirProp.GetString();
                if (!string.IsNullOrEmpty(director) && director != "N/A")
                    metadata["director"] = director;
            }

            if (movieData.TryGetProperty("Genre", out var genreProp))
            {
                var genreStr = genreProp.GetString();
                if (!string.IsNullOrEmpty(genreStr) && genreStr != "N/A")
                    metadata["genres"] = genreStr.Split(',').Select(g => g.Trim()).ToArray();
            }

            if (movieData.TryGetProperty("Rated", out var ratedProp))
            {
                var rated = ratedProp.GetString();
                if (!string.IsNullOrEmpty(rated) && rated != "N/A")
                    metadata["contentRating"] = rated;
            }

            if (movieData.TryGetProperty("Poster", out var posterProp))
            {
                var poster = posterProp.GetString();
                if (!string.IsNullOrEmpty(poster) && poster != "N/A")
                    metadata["poster"] = poster;
            }

            if (movieData.TryGetProperty("imdbRating", out var ratingProp))
            {
                var ratingStr = ratingProp.GetString();
                if (!string.IsNullOrEmpty(ratingStr) && ratingStr != "N/A" && double.TryParse(ratingStr, out var rating))
                    metadata["imdbRating"] = rating;
            }

            if (movieData.TryGetProperty("Runtime", out var runtimeProp))
            {
                var runtime = runtimeProp.GetString();
                if (!string.IsNullOrEmpty(runtime) && runtime != "N/A")
                    metadata["runtime"] = runtime;
            }

            if (movieData.TryGetProperty("Actors", out var actorsProp))
            {
                var actors = actorsProp.GetString();
                if (!string.IsNullOrEmpty(actors) && actors != "N/A")
                    metadata["cast"] = actors.Split(',').Select(a => a.Trim()).ToArray();
            }

            if (movieData.TryGetProperty("imdbID", out var imdbIdProp))
            {
                var imdbId = imdbIdProp.GetString();
                if (!string.IsNullOrEmpty(imdbId))
                    metadata["imdbId"] = imdbId;
            }

            if (movieData.TryGetProperty("Writer", out var writerProp))
            {
                var writer = writerProp.GetString();
                if (!string.IsNullOrEmpty(writer) && writer != "N/A")
                    metadata["writer"] = writer;
            }

            if (movieData.TryGetProperty("Awards", out var awardsProp))
            {
                var awards = awardsProp.GetString();
                if (!string.IsNullOrEmpty(awards) && awards != "N/A")
                    metadata["awards"] = awards;
            }

            if (movieData.TryGetProperty("BoxOffice", out var boxOfficeProp))
            {
                var boxOffice = boxOfficeProp.GetString();
                if (!string.IsNullOrEmpty(boxOffice) && boxOffice != "N/A")
                    metadata["boxOffice"] = boxOffice;
            }

            if (movieData.TryGetProperty("Production", out var productionProp))
            {
                var production = productionProp.GetString();
                if (!string.IsNullOrEmpty(production) && production != "N/A")
                    metadata["studio"] = production;
            }

            _logger.LogInformation("Successfully fetched OMDB metadata for: {Title}", title);
            return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Gets current usage info for admin display.
    /// </summary>
    public async Task<(int Used, int Limit, string Tier, bool IsExhausted)> GetUsageInfoAsync()
    {
        var tier = await _settingsService.GetSettingAsync("OMDbApiTier", "free");
        var limit = TierLimits.GetValueOrDefault(tier, 1_000);
        
        var countStr = await _settingsService.GetSettingAsync("OMDbDailyCount", "0");
        var dateStr = await _settingsService.GetSettingAsync("OMDbCountDate", "");
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

        int used = 0;
        if (dateStr == todayStr && int.TryParse(countStr, out var count))
        {
            used = count;
        }

        return (used, limit, tier, used >= limit);
    }
}
