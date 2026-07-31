using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// OMDB API provider for movie metadata.
/// Requires an API key - either the bundled SoftMedia key or a user-provided custom key.
/// Daily usage tracking (SM-WI-011: EVERY key mode — the bundled key counts against the
/// free-tier ceiling, custom keys against their configured tier) is delegated to
/// <see cref="IOmdbUsageTracker"/>; every HTTP call goes through GetOmdbResponseAsync
/// so none can bypass counting or rate limiting. Quota/key errors reported by OMDb
/// itself ("Request limit reached!", HTTP 401) suspend all OMDb calls until UTC
/// midnight instead of masquerading as "movie not found".
/// </summary>
public class OMDbProvider : IKeyedMetadataProvider, ISearchableMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OMDbProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IConfiguration _configuration;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly IOmdbUsageTracker _usageTracker;
    private readonly IProviderLookupCache? _lookupCache;

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
        INotificationService notificationService,
        IOmdbUsageTracker usageTracker,
        IProviderLookupCache? lookupCache = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("OMDb");
        _configuration = configuration;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _usageTracker = usageTracker;
        _lookupCache = lookupCache;
        
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
    /// Single funnel for every OMDb HTTP call. Acquires a rate-limiter lease, then
    /// atomically reserves one unit of the daily quota BEFORE sending — so a request
    /// that fails mid-flight can never leave the counter below OMDb's own tally.
    /// SM-WI-011: quota is counted for EVERY key mode (the bundled shared key against
    /// the free-tier ceiling), and OMDb-reported quota/key errors are recognised here:
    /// they mark the tracker exhausted (suspending all OMDb calls until UTC midnight)
    /// and return null, so callers never mistake them for "not found" and never fire
    /// the follow-up search against an already-refusing key. Returns null when the
    /// rate limiter, the daily limit, or a provider-unavailable response blocks the call.
    /// </summary>
    private async Task<string?> GetOmdbResponseAsync(string url, string mode, string context, CancellationToken ct = default)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            _logger.LogWarning("OMDB rate limit exceeded for {Context}", context);
            return null;
        }

        var limit = await GetDailyLimitAsync(mode);
        if (!await _usageTracker.TryRecordRequestAsync(limit))
        {
            _logger.LogWarning("OMDb daily limit exhausted. Skipping call for {Context}", context);
            await CreateExhaustionNotificationAsync();
            return null;
        }

        string body;
        try
        {
            body = await _httpClient.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Invalid/deactivated key. Every further call today would 401 too;
            // suspend locally instead of hammering the endpoint.
            _logger.LogError(
                "OMDb rejected the API key (401) for {Context}; suspending OMDb calls until UTC midnight", context);
            await _usageTracker.MarkExhaustedAsync(limit);
            await CreateKeyProblemNotificationAsync();
            return null;
        }

        if (IsProviderUnavailableResponse(body, out var providerError))
        {
            _logger.LogWarning(
                "OMDb unavailable for {Context} ({Error}); suspending OMDb calls until UTC midnight",
                context, providerError);
            await _usageTracker.MarkExhaustedAsync(limit);
            if (providerError.Contains("limit", StringComparison.OrdinalIgnoreCase))
                await CreateExhaustionNotificationAsync();
            else
                await CreateKeyProblemNotificationAsync();
            return null;
        }

        return body;
    }

    /// <summary>
    /// Daily ceiling for the active key mode. Custom keys use the configured tier;
    /// the bundled SoftMedia key is a free-tier key SHARED by every install, so this
    /// install counts against the free ceiling — a local stop before burning the
    /// shared quota on requests OMDb would refuse anyway.
    /// </summary>
    private async Task<int> GetDailyLimitAsync(string mode)
    {
        if (mode == "custom")
        {
            var tier = await _settingsService.GetSettingAsync("OMDbApiTier", "free");
            return TierLimits.GetValueOrDefault(tier, 1_000);
        }
        return TierLimits["free"];
    }

    /// <summary>
    /// SM-WI-011 — true when an OMDb body is a quota/key refusal rather than a lookup
    /// miss. OMDb reports both with Response:"False"; only the Error text separates
    /// "Request limit reached!" / "Invalid API key!" from "Movie not found!".
    /// Public static for direct unit testing (project convention: no InternalsVisibleTo).
    /// </summary>
    public static bool IsProviderUnavailableResponse(string body, out string providerError)
    {
        providerError = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("Response", out var resp) || resp.GetString() != "False") return false;
            if (!root.TryGetProperty("Error", out var err)) return false;

            var error = err.GetString() ?? string.Empty;
            if (error.Contains("limit reached", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("invalid api key", StringComparison.OrdinalIgnoreCase))
            {
                providerError = error;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false; // non-JSON body: let the caller's parser deal with it
        }
    }

    /// <summary>
    /// One-time notification that the configured/bundled key was rejected outright.
    /// </summary>
    private async Task CreateKeyProblemNotificationAsync()
    {
        if (!await _notificationService.HasActiveOfTypeAsync("omdb_key_invalid"))
        {
            await _notificationService.CreateAsync(
                "omdb_key_invalid",
                "OMDb API Key Rejected",
                "OMDb rejected the API key (invalid or deactivated). Movie metadata from OMDb is " +
                "suspended until midnight UTC; check the key in Settings → Metadata.",
                "error"
            );
        }
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

    /// <summary>
    /// OMDb requires an API key. Direct calls to this method bypass key resolution.
    /// All calls must go through MetadataRouter, which handles IKeyedMetadataProvider.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always thrown — use MetadataRouter instead.</exception>
    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        throw new InvalidOperationException(
            $"OMDbProvider.FetchMetadataAsync must not be called directly. " +
            $"Use MetadataRouter.FetchMetadataAsync or IKeyedMetadataProvider.FetchMetadataWithKeyAsync instead.");
    }

    /// <summary>
    /// Fetches metadata using the provided API key.
    /// </summary>
    public async Task<MetadataResult?> FetchMetadataWithKeyAsync(MediaItem item, string apiKey, string mode = "custom")
    {
        var title = item.Title;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OMDB API key is not configured for movie: {Title}", title);
            return null;
        }

        try
        {
            // 1. First, check if we already have an IMDb ID in the promoted column.
            // This allows us to skip search/fuzzy matching on refreshes and use a single direct API call.
            if (!string.IsNullOrEmpty(item.ImdbId) && item.ImdbId.StartsWith("tt"))
            {
                _logger.LogInformation("Using promoted IMDb ID for '{Title}': {Id}", title, item.ImdbId);

                // Direct ID Fetch (Cost: 1 request)
                var directUrl = $"https://www.omdbapi.com/?apikey={apiKey}&i={item.ImdbId}&plot=full";
                var directResponse = await GetOmdbResponseAsync(directUrl, mode, $"'{title}' (direct id)");
                if (directResponse == null) return null;

                using var directDoc = JsonDocument.Parse(directResponse);
                // Check for valid response
                if (directDoc.RootElement.TryGetProperty("Response", out var resp) && resp.GetString() == "True")
                {
                    // Return valid result immediately
                    return ProcessAndSerialize(directDoc.RootElement, title);
                }
                
                _logger.LogWarning("Promoted IMDb ID {Id} failed lookup, falling back to title search", item.ImdbId);
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

            // SM-WI-040: title lookups are cacheable (ID lookups above are not). A fresh
            // cached miss skips both the &t= call and the &s= fallback. Recording happens
            // ONLY at the definitive "no results" branches below — provider-unavailable
            // nulls from the funnel (quota/key suspension) must never poison the cache.
            var cacheKey = ProviderLookupCacheService.NormalizeKey("movie", cleanTitle, year);
            if (_lookupCache != null && await _lookupCache.IsFreshMissAsync(ProviderName, cacheKey))
            {
                _logger.LogDebug("OMDb: fresh cached miss for '{Title}'; skipping search", title);
                return null;
            }

            // Build search URL with full plot
            var searchUrl = $"https://www.omdbapi.com/?apikey={apiKey}&t={Uri.EscapeDataString(cleanTitle)}&type=movie&plot=full";
            if (year != null)
            {
                searchUrl += $"&y={year}";
            }

            _logger.LogInformation("Fetching OMDB data for: {Title}", title);
            var response = await GetOmdbResponseAsync(searchUrl, mode, $"'{title}'");
            if (response == null) return null;

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
                    var searchApiUrl = $"https://www.omdbapi.com/?apikey={apiKey}&s={Uri.EscapeDataString(cleanTitle)}&type=movie";
                    if (year != null)
                    {
                        searchApiUrl += $"&y={year}";
                    }

                    var searchResponse = await GetOmdbResponseAsync(searchApiUrl, mode, $"'{title}' (search fallback)");
                    if (searchResponse == null) return null;

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
                            var detailUrl = $"https://www.omdbapi.com/?apikey={apiKey}&i={imdbId}&plot=full";
                            var detailResponse = await GetOmdbResponseAsync(detailUrl, mode, $"'{title}' (detail)");
                            if (detailResponse == null) return null;

                            using var detailDoc = JsonDocument.Parse(detailResponse);
                            movieData = detailDoc.RootElement.Clone();
                        }
                        else
                        {
                            _logger.LogDebug("No OMDB results for '{Title}'", title);
                            if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
                            return null;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No OMDB results for '{Title}'", title);
                        if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
                        return null;
                    }
                }
            }

            // Build metadata dictionary using the cloned movieData
            return ProcessAndSerialize(movieData, title);
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

    private MetadataResult ProcessAndSerialize(JsonElement movieData, string title)
    {
            var result = new MetadataResult();

            if (movieData.TryGetProperty("Year", out var yearProp))
            {
                var yearStr = yearProp.GetString();
                if (!string.IsNullOrEmpty(yearStr) && yearStr != "N/A" && int.TryParse(yearStr.Substring(0, 4), out var year))
                    result.Year = year;
            }

            if (movieData.TryGetProperty("Plot", out var plotProp))
            {
                var plot = plotProp.GetString();
                if (!string.IsNullOrEmpty(plot) && plot != "N/A")
                    result.Description = plot;
            }

            if (movieData.TryGetProperty("Director", out var dirProp))
            {
                var director = dirProp.GetString();
                if (!string.IsNullOrEmpty(director) && director != "N/A")
                    result.Director = director;
            }

            if (movieData.TryGetProperty("Genre", out var genreProp))
            {
                var genreStr = genreProp.GetString();
                if (!string.IsNullOrEmpty(genreStr) && genreStr != "N/A")
                    result.Genres = genreStr.Split(',').Select(g => g.Trim()).ToList();
            }

            if (movieData.TryGetProperty("Rated", out var ratedProp))
            {
                var rated = ratedProp.GetString();
                if (!string.IsNullOrEmpty(rated) && rated != "N/A")
                    result.ContentRating = rated;
            }

            if (movieData.TryGetProperty("Poster", out var posterProp))
            {
                var poster = posterProp.GetString();
                _logger.LogInformation("Raw OMDb Poster value for {Title}: '{Poster}'", title, poster);
                if (!string.IsNullOrEmpty(poster) && poster != "N/A")
                    result.PosterUrl = poster;
            }

            if (movieData.TryGetProperty("imdbRating", out var ratingProp))
            {
                var ratingStr = ratingProp.GetString();
                if (!string.IsNullOrEmpty(ratingStr) && ratingStr != "N/A" && double.TryParse(ratingStr, out var rating))
                    result.ImdbRating = rating;
            }

            if (movieData.TryGetProperty("Actors", out var actorsProp))
            {
                var actors = actorsProp.GetString();
                if (!string.IsNullOrEmpty(actors) && actors != "N/A")
                    result.Cast = actors.Split(',').Select(a => new CastMember { Name = a.Trim() }).ToList();
            }

            if (movieData.TryGetProperty("imdbID", out var imdbIdProp))
            {
                var imdbId = imdbIdProp.GetString();
                if (!string.IsNullOrEmpty(imdbId))
                    result.ImdbId = imdbId;
            }

            if (movieData.TryGetProperty("Production", out var productionProp))
            {
                var production = productionProp.GetString();
                if (!string.IsNullOrEmpty(production) && production != "N/A")
                    result.Studio = production;
            }

            // SM-WI-044 (Q1 decision): the old Extra block (runtime/writer/awards/
            // boxOffice) was computed on every fetch and then dropped — the aggregator
            // persists Extra only for photos. Removed rather than persisted: no column,
            // no consumer, pure cost.

            _logger.LogInformation("Successfully fetched OMDB metadata for: {Title}", title);
            return result;
    }

    /// <summary>
    /// Gets current usage info for admin display.
    /// </summary>
    public async Task<(int Used, int Limit, string Tier, bool IsExhausted)> GetUsageInfoAsync()
    {
        var tier = await _settingsService.GetSettingAsync("OMDbApiTier", "free");
        var limit = TierLimits.GetValueOrDefault(tier, 1_000);
        var used = await _usageTracker.GetUsedTodayAsync();

        return (used, limit, tier, used >= limit);
    }

    // --- ISearchableMetadataProvider (P3-WI-003 Fix Match) ---

    public async Task<IReadOnlyList<MetadataSearchCandidate>> SearchAsync(string query, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MetadataSearchCandidate>();

        var mode = await _settingsService.GetSettingAsync("OMDbApiKeyMode", "softmedia");
        var customKey = await _settingsService.GetSettingAsync("OMDbApiKeyCustom", "");
        var apiKey = GetActiveApiKey(mode, customKey);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("OMDb search aborted: no active API key.");
            return Array.Empty<MetadataSearchCandidate>();
        }

        // Same endpoint shape as the fallback search inside FetchMetadataWithKeyAsync,
        // just exposed publicly + returns multiple candidates rather than auto-picking
        // the first. Goes through the counted funnel like every other OMDb call.
        var url = $"https://www.omdbapi.com/?apikey={apiKey}&s={Uri.EscapeDataString(query.Trim())}&type=movie";
        if (year.HasValue) url += $"&y={year}";

        string? body;
        try { body = await GetOmdbResponseAsync(url, mode, $"search '{query}'", ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OMDb search failed for '{Query}'", query);
            return Array.Empty<MetadataSearchCandidate>();
        }
        if (body == null) return Array.Empty<MetadataSearchCandidate>();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Search", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return Array.Empty<MetadataSearchCandidate>();

        var candidates = new List<MetadataSearchCandidate>();
        foreach (var r in results.EnumerateArray())
        {
            var imdb = r.TryGetProperty("imdbID", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(imdb)) continue;

            int? parsedYear = null;
            if (r.TryGetProperty("Year", out var yEl) && yEl.ValueKind == JsonValueKind.String)
            {
                var yStr = yEl.GetString() ?? "";
                // OMDb returns "1995", "1995–1999", or "1995–"; take the first 4 chars.
                if (yStr.Length >= 4 && int.TryParse(yStr.Substring(0, 4), out var y)) parsedYear = y;
            }

            string? poster = null;
            if (r.TryGetProperty("Poster", out var posterEl) && posterEl.ValueKind == JsonValueKind.String)
            {
                var p = posterEl.GetString();
                if (!string.IsNullOrEmpty(p) && p != "N/A") poster = p;
            }

            candidates.Add(new MetadataSearchCandidate(
                ProviderName,
                imdb!,
                r.GetProperty("Title").GetString() ?? "(untitled)",
                parsedYear,
                poster,
                r.TryGetProperty("Type", out var t) ? t.GetString() : null));

            if (candidates.Count >= 10) break;
        }
        return candidates;
    }

    /// <summary>
    /// Fetch full metadata for an IMDb-id candidate. Resolves the API key itself
    /// (FetchMetadataAsync intentionally throws to force key resolution) and reuses
    /// FetchMetadataWithKeyAsync's promoted-IMDb-id short-circuit, so the call is
    /// counted and rate-limited like every other OMDb request.
    /// </summary>
    public async Task<MetadataResult?> FetchByCandidateAsync(string providerItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerItemId) || !providerItemId.StartsWith("tt"))
            return null;

        var mode = await _settingsService.GetSettingAsync("OMDbApiKeyMode", "softmedia");
        var customKey = await _settingsService.GetSettingAsync("OMDbApiKeyCustom", "");
        var apiKey = GetActiveApiKey(mode, customKey);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("OMDb fix-match fetch aborted: no active API key.");
            return null;
        }

        return await FetchMetadataWithKeyAsync(new MediaItem
        {
            Title = "(fix-match)",
            Type = Models.MediaType.Movie,
            ImdbId = providerItemId,
        }, apiKey, mode);
    }
}
