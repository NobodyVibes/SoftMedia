using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for comic book series (Q1004).
/// Scoped to LibraryType.Book and only runs for ComicSeries / ComicIssue items, so
/// ebooks in a Book library are never queried. Series-level data only — Wikidata
/// rarely has per-issue records.
///
/// References:
///   - WDQS endpoint: https://query.wikidata.org/sparql
///   - User-Agent policy: https://meta.wikimedia.org/wiki/User-Agent_policy
///   - Query service manual: https://www.mediawiki.org/wiki/Wikidata_Query_Service/User_Manual
/// </summary>
public class ComicWikidataProvider : WikidataSparqlClient
{
    public override LibraryType SupportedType => LibraryType.Book;
    public override string ProviderName => "Wikidata";

    public ComicWikidataProvider(HttpClient httpClient, ILogger<ComicWikidataProvider> logger, RateLimiterFactory rateLimiterFactory)
        : base(httpClient, logger, rateLimiterFactory) { }

    protected override bool ShouldFetch(MediaItem item)
    {
        // Only fire for ComicSeries. Wikidata has no per-issue records, and a
        // ComicIssue's own Title is something like "Issue #5" which produces nonsense
        // searches. Resolving the parent series from SeriesId would require DB access
        // we don't have here — and even then the series itself gets enriched separately.
        return item.Type == MediaType.ComicSeries;
    }

    protected override string BuildSparqlQuery(MediaItem item)
    {
        // For an issue we still search by the series title (Wikidata has no issue records).
        // The MediaItem has a SeriesId pointing at the parent, but we only have primitive
        // access here; the series name is embedded in the parent's Title which the router
        // will have resolved by the time the issue is queried. Fallback is the item's own
        // Title minus any "Issue #N" suffix.
        var searchTitle = ExtractSearchTitle(item);
        var safeTitle = EscapeForSparql(searchTitle);

        // Q1004 = comic book series. wdt:P31/wdt:P279* walks the subclass tree so
        // manga series, webcomics etc. are also matched.
        return $@"
            SELECT ?item ?itemLabel
                   (SAMPLE(?inception) AS ?year)
                   (SAMPLE(?publisherLabel) AS ?publisher)
                   (GROUP_CONCAT(DISTINCT ?genreLabel; SEPARATOR="", "") AS ?genres)
                   (SAMPLE(?description) AS ?description)
                   (SAMPLE(?image) AS ?image)
            WHERE {{
              SERVICE wikibase:mwapi {{
                  bd:serviceParam wikibase:api ""EntitySearch"" .
                  bd:serviceParam wikibase:endpoint ""www.wikidata.org"" .
                  bd:serviceParam mwapi:search ""{safeTitle}"" .
                  bd:serviceParam mwapi:language ""en"" .
                  ?item wikibase:apiOutputItem mwapi:item .
              }}
              ?item wdt:P31/wdt:P279* wd:Q1004 .

              OPTIONAL {{ ?item wdt:P571 ?pub . BIND(YEAR(?pub) AS ?inception) }}
              OPTIONAL {{ ?item wdt:P123 ?publisher . ?publisher rdfs:label ?publisherLabel . FILTER(LANG(?publisherLabel) = ""en"") }}
              OPTIONAL {{ ?item wdt:P136 ?genre . ?genre rdfs:label ?genreLabel . FILTER(LANG(?genreLabel) = ""en"") }}
              OPTIONAL {{ ?item schema:description ?description . FILTER(LANG(?description) = ""en"") }}
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

        var description = GetBindingString(result, "description");
        if (!string.IsNullOrWhiteSpace(description))
            metadata.Description = description;

        var publisher = GetBindingString(result, "publisher");
        if (!string.IsNullOrWhiteSpace(publisher))
        {
            metadata.Publisher = publisher;
            metadata.Studio = publisher;
        }

        var genres = GetBindingString(result, "genres");
        if (!string.IsNullOrWhiteSpace(genres))
            metadata.Genres = genres.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();

        var image = GetBindingString(result, "image");
        if (!string.IsNullOrWhiteSpace(image))
            metadata.PosterUrl = image;

        return metadata;
    }

    /// <summary>
    /// Strips issue-number noise from the query title so lookups hit the series.
    /// "Amazing Man Comics Issue 005" → "Amazing Man Comics"
    /// "Issue #5"                     → "" (caller should have series context)
    /// </summary>
    public static string ExtractSearchTitle(MediaItem item)
    {
        var raw = item.Title ?? string.Empty;

        // Drop common issue-number trailers.
        var trimmed = System.Text.RegularExpressions.Regex.Replace(
            raw,
            @"\s+(?:Issue|Iss\.?|No\.?|Number)\s+\d+\s*$|\s*#\s*\d+\s*$|\s+\d{1,4}\s*(?:\(\d{4}\))?\s*$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? raw.Trim() : trimmed;
    }

    // SM-WI-012: EscapeForSparql moved to WikidataSparqlClient (this class's private
    // copy was the only escaping in the codebase; the shared one now serves everyone).
}
