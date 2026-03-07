using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for video game metadata.
/// Searches Wikidata for video games (Q7889) to fetch developer, publisher, platform, and genre info.
/// </summary>
public class GameMetadataProvider : WikidataSparqlClient
{
    public override LibraryType SupportedType => LibraryType.Game;
    public override string ProviderName => "Wikidata";

    public GameMetadataProvider(HttpClient httpClient, ILogger<GameMetadataProvider> logger, RateLimiterFactory rateLimiterFactory)
        : base(httpClient, logger, rateLimiterFactory) { }

    protected override string BuildSparqlQuery(MediaItem item)
    {
        // Entity search filtered to video games (Q7889)
        var itemSelector = BuildEntitySearchSelector(item.Title, "Q7889");

        return $@"
            SELECT DISTINCT ?item ?itemLabel ?year ?developerLabel ?publisherLabel ?platformLabel ?genreLabel ?modeLabel ?description ?logo ?image WHERE {{
              {itemSelector}
              
              OPTIONAL {{ ?item wdt:P577 ?pubDate . BIND(YEAR(?pubDate) AS ?year) }}
              OPTIONAL {{ ?item wdt:P178 ?developer . }}
              OPTIONAL {{ ?item wdt:P123 ?publisher . }}
              OPTIONAL {{ ?item wdt:P400 ?platform . }}
              OPTIONAL {{ ?item wdt:P136 ?genre . }}
              OPTIONAL {{ ?item wdt:P404 ?mode . }}
              OPTIONAL {{ ?item wdt:P154 ?logo . }}
              OPTIONAL {{ ?item wdt:P18 ?image . }}
              OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
              
              SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
            }}
            LIMIT 1
        ";
    }

    protected override MetadataResult ExtractMetadata(JsonElement result, MediaItem item)
    {
        var metadata = new MetadataResult();

        if (int.TryParse(GetBindingString(result, "year"), out var year))
            metadata.Year = year;

        metadata.Description = GetBindingString(result, "description");
        metadata.Studio = GetBindingString(result, "developerLabel");
        metadata.Publisher = GetBindingString(result, "publisherLabel");

        var logo = GetBindingString(result, "logo");
        var image = GetBindingString(result, "image");
        metadata.PosterUrl = string.IsNullOrEmpty(logo) ? (string.IsNullOrEmpty(image) ? null : image) : logo;

        var genre = GetBindingString(result, "genreLabel");
        if (!string.IsNullOrEmpty(genre))
            metadata.Genres = new List<string> { genre };

        var platform = GetBindingString(result, "platformLabel");
        var gameMode = GetBindingString(result, "modeLabel");

        if (!string.IsNullOrEmpty(platform) || !string.IsNullOrEmpty(gameMode))
        {
            metadata.Extra = new Dictionary<string, JsonElement>();
            if (!string.IsNullOrEmpty(platform))
                metadata.Extra["platform"] = JsonSerializer.SerializeToElement(platform);
            if (!string.IsNullOrEmpty(gameMode))
                metadata.Extra["gameMode"] = JsonSerializer.SerializeToElement(gameMode);
        }

        return metadata;
    }
}
