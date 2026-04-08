using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for video game metadata.
/// Searches Wikidata for video games (Q7889) to fetch developer, publisher, platform, and genre info.
/// Uses GROUP_CONCAT for multi-valued properties (genre, platform, game mode).
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

        // GROUP_CONCAT aggregates multi-valued properties into comma-separated strings.
        return $@"
            SELECT ?item ?itemLabel
                   (SAMPLE(?year) AS ?year)
                   (SAMPLE(?developerLabel) AS ?developerLabel)
                   (SAMPLE(?publisherLabel) AS ?publisherLabel)
                   (GROUP_CONCAT(DISTINCT ?platformLabel; SEPARATOR="", "") AS ?platforms)
                   (GROUP_CONCAT(DISTINCT ?genreLabel; SEPARATOR="", "") AS ?genres)
                   (GROUP_CONCAT(DISTINCT ?modeLabel; SEPARATOR="", "") AS ?modes)
                   (SAMPLE(?description) AS ?description)
                   (SAMPLE(?logo) AS ?logo)
                   (SAMPLE(?image) AS ?image)
            WHERE {{
              {itemSelector}
              
              OPTIONAL {{ ?item wdt:P577 ?pubDate . BIND(YEAR(?pubDate) AS ?year) }}
              OPTIONAL {{ ?item wdt:P178 ?developer . ?developer rdfs:label ?developerLabel . FILTER(LANG(?developerLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P123 ?publisher . ?publisher rdfs:label ?publisherLabel . FILTER(LANG(?publisherLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P400 ?platform . ?platform rdfs:label ?platformLabel . FILTER(LANG(?platformLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P136 ?genre . ?genre rdfs:label ?genreLabel . FILTER(LANG(?genreLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P404 ?mode . ?mode rdfs:label ?modeLabel . FILTER(LANG(?modeLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P154 ?logo . }}
              OPTIONAL {{ ?item wdt:P18 ?image . }}
              OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
              
              SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
            }}
            GROUP BY ?item ?itemLabel
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

        // Parse aggregated genres (comma-separated) into list
        var genres = GetBindingString(result, "genres");
        if (!string.IsNullOrEmpty(genres))
            metadata.Genres = genres.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();

        // Map aggregated platforms and game modes to typed MetadataResult properties
        var platforms = GetBindingString(result, "platforms");
        var gameModes = GetBindingString(result, "modes");

        if (!string.IsNullOrEmpty(platforms))
            metadata.Platform = platforms;
        if (!string.IsNullOrEmpty(gameModes))
            metadata.GameMode = gameModes;

        return metadata;
    }
}
