using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

public class WikidataProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WikidataProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public LibraryType SupportedType => LibraryType.Movie;
    public string ProviderName => "Wikidata";

    public WikidataProvider(HttpClient httpClient, ILogger<WikidataProvider> logger, RateLimiterFactory rateLimiterFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("Wikidata");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;
        try
        {
            // Acquire rate limit lease before making API call
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning($"Wikidata rate limit exceeded for '{title}', request was queued too long");
                return null;
            }
            
            // Check for cached IMDb ID first
            string itemSelector;
            string? imdbId = null;
            
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try 
                {
                    using var existingDoc = JsonDocument.Parse(item.MetadataJson);
                    if (existingDoc.RootElement.TryGetProperty("imdbId", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id) && id.StartsWith("tt"))
                        {
                            imdbId = id;
                        }
                    }
                }
                catch {}
            }

            if (imdbId != null)
            {
                _logger.LogInformation($"Using cached IMDb ID for '{title}': {imdbId}");
                // Direct lookup by IMDb ID (P345)
                // Note: We don't strictly filter by Q11424 (film) here because the ID is specific enough, 
                // but we might want to ensure it's a creative work if needed. For now, trusting the ID is safe.
                itemSelector = $"?item wdt:P345 \"{imdbId}\" .";
            }
            else
            {
                // Fallback: SPARQL Text Search
                // Filters: instance of (P31) film (Q11424) or subclass
                itemSelector = $@"
                  SERVICE wikibase:mwapi {{
                      bd:serviceParam wikibase:api ""EntitySearch"" .
                      bd:serviceParam wikibase:endpoint ""www.wikidata.org"" .
                      bd:serviceParam mwapi:search ""{title}"" .
                      bd:serviceParam mwapi:language ""en"" .
                      ?item wikibase:apiOutputItem mwapi:item .
                  }}
                  ?item wdt:P31/wdt:P279* wd:Q11424 .
                ";
            }
            
            // Optional: Director (P57), Cast (P161), Pub Date (P577), Genre (P136), MPAA (P1657)
            var sparqlQuery = $@"
                SELECT DISTINCT ?item ?itemLabel ?year ?directorLabel ?genreLabel ?mpaaLabel ?description ?poster ?image WHERE {{
                  {itemSelector}
                  
                  OPTIONAL {{ ?item wdt:P577 ?pubDate . BIND(YEAR(?pubDate) AS ?year) }}
                  OPTIONAL {{ ?item wdt:P57 ?director . }}
                  OPTIONAL {{ ?item wdt:P136 ?genre . }}
                  OPTIONAL {{ ?item wdt:P1657 ?mpaa . }}
                  OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
                  OPTIONAL {{ ?item wdt:P3383 ?poster . }}
                  OPTIONAL {{ ?item wdt:P18 ?image . }}
                  
                  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
                }}
                LIMIT 1
            ";

            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(sparqlQuery)}&format=json";
            _logger.LogInformation($"Fetching Wikidata for {title}: {url}");
            var response = await _httpClient.GetStringAsync(url);
            
            using var doc = JsonDocument.Parse(response);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            
            if (bindings.GetArrayLength() == 0) return null;

            var result = bindings[0];
            
            var metadata = new Dictionary<string, object>();
            
            if (result.TryGetProperty("year", out var yearProp)) 
            {
                var year = yearProp.GetProperty("value").GetString();
                if (year != null) metadata["year"] = year;
            }
                
            if (result.TryGetProperty("description", out var descProp)) 
            {
                var desc = descProp.GetProperty("value").GetString();
                if (desc != null) metadata["description"] = desc;
            }
                
            if (result.TryGetProperty("directorLabel", out var dirProp)) 
            {
                var dir = dirProp.GetProperty("value").GetString();
                if (dir != null) metadata["director"] = dir;
            }
                
            if (result.TryGetProperty("genreLabel", out var genreProp)) 
            {
                var genre = genreProp.GetProperty("value").GetString();
                if (genre != null) metadata["genres"] = new[] { genre };
            }
                
            if (result.TryGetProperty("mpaaLabel", out var mpaaProp)) 
            {
                var mpaa = mpaaProp.GetProperty("value").GetString();
                if (mpaa != null) metadata["contentRating"] = mpaa;
            }

            // Prefer official movie poster (P3383), fallback to image (P18)
            string? posterUrl = null;
            if (result.TryGetProperty("poster", out var posterProp))
            {
                posterUrl = posterProp.GetProperty("value").GetString();
            }
            else if (result.TryGetProperty("image", out var imageProp))
            {
                posterUrl = imageProp.GetProperty("value").GetString();
            }

            if (posterUrl != null)
            {
                metadata["poster"] = posterUrl;
            }

            return JsonSerializer.Serialize(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Wikidata for {title}");
            return null;
        }
    }
}
