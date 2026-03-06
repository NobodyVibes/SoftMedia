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
            SELECT DISTINCT ?item ?itemLabel ?year ?developerLabel ?publisherLabel ?platformLabel ?genreLabel ?modeLabel ?description WHERE {{
              {itemSelector}
              
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
    }

    protected override Dictionary<string, object> ExtractMetadata(JsonElement result, MediaItem item)
    {
        var metadata = new Dictionary<string, object>();

        TryAddBinding(metadata, result, "year", "year");
        TryAddBinding(metadata, result, "description", "description");
        TryAddBinding(metadata, result, "developerLabel", "studio");   // Map Developer to Studio
        TryAddBinding(metadata, result, "publisherLabel", "publisher");
        TryAddBinding(metadata, result, "platformLabel", "platform");
        TryAddBindingArray(metadata, result, "genreLabel", "genres");
        TryAddBinding(metadata, result, "modeLabel", "gameMode");

        return metadata;
    }
}
