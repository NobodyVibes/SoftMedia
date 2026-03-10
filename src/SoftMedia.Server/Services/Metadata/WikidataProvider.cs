using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for movie metadata.
/// Searches Wikidata for films (Q11424), with optional cached IMDb ID shortcut.
/// Uses GROUP_CONCAT for multi-valued properties (genre, director) to aggregate all values.
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

        // GROUP_CONCAT aggregates multi-valued properties (genre, director) into comma-separated strings.
        // GROUP BY on single-valued properties ensures one row per film.
        return $@"
            SELECT ?item ?itemLabel
                   (SAMPLE(?year) AS ?year)
                   (GROUP_CONCAT(DISTINCT ?directorLabel; SEPARATOR="", "") AS ?directors)
                   (GROUP_CONCAT(DISTINCT ?genreLabel; SEPARATOR="", "") AS ?genres)
                   (SAMPLE(?mpaaLabel) AS ?mpaaLabel)
                   (SAMPLE(?description) AS ?description)
                   (SAMPLE(?poster) AS ?poster)
                   (SAMPLE(?image) AS ?image)
            WHERE {{
              {itemSelector}
              
              OPTIONAL {{ ?item wdt:P577 ?pubDate . BIND(YEAR(?pubDate) AS ?year) }}
              OPTIONAL {{ ?item wdt:P57 ?director . ?director rdfs:label ?directorLabel . FILTER(LANG(?directorLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P136 ?genre . ?genre rdfs:label ?genreLabel . FILTER(LANG(?genreLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P1657 ?mpaa . ?mpaa rdfs:label ?mpaaLabel . FILTER(LANG(?mpaaLabel) = ""en"") }}
              OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
              OPTIONAL {{ ?item wdt:P3383 ?poster . }}
              OPTIONAL {{ ?item wdt:P18 ?image . }}
              
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

        // Parse aggregated directors (comma-separated)
        var directors = GetBindingString(result, "directors");
        if (!string.IsNullOrEmpty(directors))
            metadata.Director = directors;
        
        // Parse aggregated genres (comma-separated) into list
        var genres = GetBindingString(result, "genres");
        if (!string.IsNullOrEmpty(genres))
            metadata.Genres = genres.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();

        metadata.ContentRating = GetBindingString(result, "mpaaLabel");

        // Prefer official movie poster (P3383), fallback to image (P18)
        var posterUrl = GetBindingString(result, "poster") ?? GetBindingString(result, "image");
        if (posterUrl != null)
            metadata.PosterUrl = posterUrl;

        return metadata;
    }
}
