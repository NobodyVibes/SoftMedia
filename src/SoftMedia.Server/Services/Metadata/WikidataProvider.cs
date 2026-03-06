using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for movie metadata.
/// Searches Wikidata for films (Q11424), with optional cached IMDb ID shortcut.
/// </summary>
public class WikidataProvider : WikidataSparqlClient
{
    public override LibraryType SupportedType => LibraryType.Movie;
    public override string ProviderName => "Wikidata";

    public WikidataProvider(HttpClient httpClient, ILogger<WikidataProvider> logger, RateLimiterFactory rateLimiterFactory)
        : base(httpClient, logger, rateLimiterFactory) { }

    protected override string BuildSparqlQuery(MediaItem item)
    {
        string itemSelector;

        // Check for cached IMDb ID for direct lookup (faster, more accurate)
        var imdbId = TryGetCachedId(item, "imdbId", "tt");
        if (imdbId != null)
        {
            Logger.LogInformation("Using cached IMDb ID for '{Title}': {ImdbId}", item.Title, imdbId);
            itemSelector = $"?item wdt:P345 \"{imdbId}\" .";
        }
        else
        {
            // Fallback: entity search filtered to films (Q11424)
            itemSelector = BuildEntitySearchSelector(item.Title, "Q11424");
        }

        return $@"
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
    }

    protected override Dictionary<string, object> ExtractMetadata(JsonElement result, MediaItem item)
    {
        var metadata = new Dictionary<string, object>();

        TryAddBinding(metadata, result, "year", "year");
        TryAddBinding(metadata, result, "description", "description");
        TryAddBinding(metadata, result, "directorLabel", "director");
        TryAddBindingArray(metadata, result, "genreLabel", "genres");
        TryAddBinding(metadata, result, "mpaaLabel", "contentRating");

        // Prefer official movie poster (P3383), fallback to image (P18)
        var posterUrl = GetBindingString(result, "poster") ?? GetBindingString(result, "image");
        if (posterUrl != null)
            metadata["poster"] = posterUrl;

        return metadata;
    }
}
