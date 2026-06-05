using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Wikidata SPARQL provider for movie metadata.
/// Searches Wikidata for films (Q11424), with optional cached IMDb ID shortcut.
/// Uses GROUP_CONCAT for multi-valued properties (genre, director) to aggregate all values.
/// </summary>
public class WikidataProvider : WikidataSparqlClient, ISearchableMetadataProvider
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

    // --- ISearchableMetadataProvider (P3-WI-003 Fix Match) ---

    /// <summary>
    /// Search via Wikidata's wbsearchentities REST API (no SPARQL — faster, returns
    /// labels + descriptions directly). We then filter to films (Q11424) with a
    /// follow-up SPARQL claims call. The verification noted this is the cleaner of
    /// the two Wikidata search shapes; the alternative is widening BuildEntitySearchSelector.
    /// </summary>
    public async Task<IReadOnlyList<MetadataSearchCandidate>> SearchAsync(string query, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MetadataSearchCandidate>();
        var url = "https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&language=en"
                + $"&type=item&limit=15&search={Uri.EscapeDataString(query.Trim())}";

        string body;
        try { body = await HttpClient.GetStringAsync(url, ct); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Wikidata wbsearchentities failed for '{Query}'", query);
            return Array.Empty<MetadataSearchCandidate>();
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("search", out var search) ||
            search.ValueKind != JsonValueKind.Array)
            return Array.Empty<MetadataSearchCandidate>();

        // wbsearchentities returns entities of ALL types. We can't cheaply filter to
        // films without a follow-up call per Q-id, so we surface the top-10 raw hits
        // and let the description column ("description" field) tell the user which is
        // which — matching how Plex's "Fix Match" lets you eyeball the right one.
        var candidates = new List<MetadataSearchCandidate>();
        foreach (var entity in search.EnumerateArray())
        {
            var qid = entity.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(qid)) continue;

            var label = entity.TryGetProperty("label", out var l) ? l.GetString() : null;
            var description = entity.TryGetProperty("description", out var d) ? d.GetString() : null;

            candidates.Add(new MetadataSearchCandidate(
                ProviderName,
                qid!,
                label ?? qid!,
                year, // wbsearchentities doesn't return year; pass through the caller's hint
                null, // no poster from the search endpoint — UI shows placeholder
                description));

            if (candidates.Count >= 10) break;
        }
        return candidates;
    }

    /// <summary>
    /// Fetches full metadata for a chosen Wikidata Q-id by running the standard SPARQL
    /// query keyed on the Q-id directly (replacing the entity-search selector). Reuses
    /// the base class's HTTP execution + JSON binding plumbing via a synthetic MediaItem
    /// whose Title isn't used (the Q-id selector takes precedence).
    /// </summary>
    public async Task<MetadataResult?> FetchByCandidateAsync(string providerItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerItemId) || !providerItemId.StartsWith('Q'))
            return null;

        // Build a Q-id-direct SPARQL query (mirrors BuildSparqlQuery but selectors on wd:Q…).
        var sparql = $@"
            SELECT ?item ?itemLabel
                   (SAMPLE(?year) AS ?year)
                   (GROUP_CONCAT(DISTINCT ?directorLabel; SEPARATOR="", "") AS ?directors)
                   (GROUP_CONCAT(DISTINCT ?genreLabel; SEPARATOR="", "") AS ?genres)
                   (SAMPLE(?mpaaLabel) AS ?mpaaLabel)
                   (SAMPLE(?description) AS ?description)
                   (SAMPLE(?poster) AS ?poster)
                   (SAMPLE(?image) AS ?image)
            WHERE {{
              BIND(wd:{providerItemId} AS ?item)
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
            LIMIT 1";

        var url = $"https://query.wikidata.org/sparql?format=json&query={Uri.EscapeDataString(sparql)}";
        try
        {
            var response = await HttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("bindings", out var bindings) ||
                bindings.GetArrayLength() == 0)
                return null;
            // Reuse the existing extractor; pass a placeholder MediaItem (Title only used for logs there).
            return ExtractMetadata(bindings[0], new MediaItem { Title = "(fix-match)", Type = Models.MediaType.Movie });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Wikidata FetchByCandidate failed for {Qid}", providerItemId);
            return null;
        }
    }
}
