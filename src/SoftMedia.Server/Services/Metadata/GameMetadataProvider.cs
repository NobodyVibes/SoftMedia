using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class GameMetadataProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GameMetadataProvider> _logger;

    public LibraryType SupportedType => LibraryType.Game;
    public string ProviderName => "Wikidata";

    public GameMetadataProvider(HttpClient httpClient, ILogger<GameMetadataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        var path = item.Path;
        try
        {
            // SPARQL Query to find game by title and get details
            // Filters: instance of (P31) video game (Q7889) or subclass
            var sparqlQuery = $@"
                SELECT DISTINCT ?item ?itemLabel ?year ?developerLabel ?publisherLabel ?platformLabel ?genreLabel ?modeLabel ?description WHERE {{
                  SERVICE wikibase:mwapi {{
                      bd:serviceParam wikibase:api ""EntitySearch"" .
                      bd:serviceParam wikibase:endpoint ""www.wikidata.org"" .
                      bd:serviceParam mwapi:search ""{title}"" .
                      bd:serviceParam mwapi:language ""en"" .
                      ?item wikibase:apiOutputItem mwapi:item .
                  }}
                  ?item wdt:P31/wdt:P279* wd:Q7889 .
                  
                  OPTIONAL {{ ?item wdt:P577 ?pubDate . BIND(YEAR(?pubDate) AS ?year) }}
                  OPTIONAL {{ ?item wdt:P178 ?developer . }}
                  OPTIONAL {{ ?item wdt:P123 ?publisher . }}
                  OPTIONAL {{ ?item wdt:P400 ?platform . }}
                  OPTIONAL {{ ?item wdt:P136 ?genre . }}
                  OPTIONAL {{ ?item wdt:P404 ?mode . }}
                  OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
                  
                  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
                }}
                LIMIT 1
            ";

            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(sparqlQuery)}&format=json";
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
                
            if (result.TryGetProperty("developerLabel", out var devProp)) 
            {
                var dev = devProp.GetProperty("value").GetString();
                if (dev != null) metadata["studio"] = dev; // Map Developer to Studio
            }
                
            if (result.TryGetProperty("publisherLabel", out var pubProp)) 
            {
                var pub = pubProp.GetProperty("value").GetString();
                if (pub != null) metadata["publisher"] = pub;
            }
                
            if (result.TryGetProperty("platformLabel", out var platProp)) 
            {
                var plat = platProp.GetProperty("value").GetString();
                if (plat != null) metadata["platform"] = plat;
            }
                
            if (result.TryGetProperty("genreLabel", out var genreProp)) 
            {
                var genre = genreProp.GetProperty("value").GetString();
                if (genre != null) metadata["genres"] = new[] { genre };
            }
                
            if (result.TryGetProperty("modeLabel", out var modeProp)) 
            {
                var mode = modeProp.GetProperty("value").GetString();
                if (mode != null) metadata["gameMode"] = mode;
            }

            return JsonSerializer.Serialize(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Game metadata for {title}");
            return null;
        }
    }
}
