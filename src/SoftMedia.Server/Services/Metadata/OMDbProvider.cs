using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// OMDB API provider for movie metadata.
/// Requires an API key - either the bundled SoftMedia key or a user-provided custom key.
/// </summary>
public class OMDbProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OMDbProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IConfiguration _configuration;

    public LibraryType SupportedType => LibraryType.Movie;
    public string ProviderName => "OMDb";

    // Placeholder for SoftMedia bundled key - see docs/OMDB_API_KEY_SETUP.md
    private const string SOFTMEDIA_KEY_PLACEHOLDER = "SOFTMEDIA_OMDB_KEY_PLACEHOLDER";

    public OMDbProvider(
        HttpClient httpClient, 
        ILogger<OMDbProvider> logger, 
        RateLimiterFactory rateLimiterFactory,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("OMDb");
        _configuration = configuration;
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

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        // This method is called by MetadataRouter which should pass the resolved API key
        // For direct calls, we return null as the key context isn't available
        _logger.LogDebug("FetchMetadataAsync called directly - use FetchMetadataWithKeyAsync instead");
        return null;
    }

    /// <summary>
    /// Fetches metadata using the provided API key.
    /// </summary>
    public async Task<string?> FetchMetadataWithKeyAsync(MediaItem item, string apiKey)
    {
        var title = item.Title;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OMDB API key is not configured for movie: {Title}", title);
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

            // Extract year from title if present (e.g., "Movie Name (2023)")
            string? year = null;
            var cleanTitle = title;
            var yearMatch = System.Text.RegularExpressions.Regex.Match(title, @"\((\d{4})\)$");
            if (yearMatch.Success)
            {
                year = yearMatch.Groups[1].Value;
                cleanTitle = title.Substring(0, yearMatch.Index).Trim();
            }

            // Build search URL
            var searchUrl = $"http://www.omdbapi.com/?apikey={apiKey}&t={Uri.EscapeDataString(cleanTitle)}&type=movie";
            if (year != null)
            {
                searchUrl += $"&y={year}";
            }

            _logger.LogInformation("Fetching OMDB data for: {Title}", title);
            var response = await _httpClient.GetStringAsync(searchUrl);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Check for API error
            if (root.TryGetProperty("Error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger.LogWarning("OMDB API error for '{Title}': {Error}", title, error);
                return null;
            }

            // Check if found
            if (!root.TryGetProperty("Response", out var responseProp) || responseProp.GetString() != "True")
            {
                _logger.LogDebug("No OMDB results for '{Title}'", title);
                return null;
            }

            // Build metadata dictionary
            var metadata = new Dictionary<string, object>();

            if (root.TryGetProperty("Year", out var yearProp))
            {
                var yearStr = yearProp.GetString();
                if (!string.IsNullOrEmpty(yearStr) && yearStr != "N/A")
                    metadata["year"] = yearStr;
            }

            if (root.TryGetProperty("Plot", out var plotProp))
            {
                var plot = plotProp.GetString();
                if (!string.IsNullOrEmpty(plot) && plot != "N/A")
                    metadata["description"] = plot;
            }

            if (root.TryGetProperty("Director", out var dirProp))
            {
                var director = dirProp.GetString();
                if (!string.IsNullOrEmpty(director) && director != "N/A")
                    metadata["director"] = director;
            }

            if (root.TryGetProperty("Genre", out var genreProp))
            {
                var genreStr = genreProp.GetString();
                if (!string.IsNullOrEmpty(genreStr) && genreStr != "N/A")
                    metadata["genres"] = genreStr.Split(',').Select(g => g.Trim()).ToArray();
            }

            if (root.TryGetProperty("Rated", out var ratedProp))
            {
                var rated = ratedProp.GetString();
                if (!string.IsNullOrEmpty(rated) && rated != "N/A")
                    metadata["contentRating"] = rated;
            }

            if (root.TryGetProperty("Poster", out var posterProp))
            {
                var poster = posterProp.GetString();
                if (!string.IsNullOrEmpty(poster) && poster != "N/A")
                    metadata["poster"] = poster;
            }

            if (root.TryGetProperty("imdbRating", out var ratingProp))
            {
                var ratingStr = ratingProp.GetString();
                if (!string.IsNullOrEmpty(ratingStr) && ratingStr != "N/A" && double.TryParse(ratingStr, out var rating))
                    metadata["rating"] = rating;
            }

            if (root.TryGetProperty("Runtime", out var runtimeProp))
            {
                var runtime = runtimeProp.GetString();
                if (!string.IsNullOrEmpty(runtime) && runtime != "N/A")
                    metadata["runtime"] = runtime;
            }

            if (root.TryGetProperty("Actors", out var actorsProp))
            {
                var actors = actorsProp.GetString();
                if (!string.IsNullOrEmpty(actors) && actors != "N/A")
                    metadata["cast"] = actors.Split(',').Select(a => a.Trim()).ToArray();
            }

            if (root.TryGetProperty("imdbID", out var imdbIdProp))
            {
                var imdbId = imdbIdProp.GetString();
                if (!string.IsNullOrEmpty(imdbId))
                    metadata["imdbId"] = imdbId;
            }

            _logger.LogInformation("Successfully fetched OMDB metadata for: {Title}", title);
            return JsonSerializer.Serialize(metadata);
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
}
