using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Base class for Wikidata SPARQL-based metadata providers.
/// Encapsulates shared HTTP setup, rate limiting, query execution, and JSON parsing.
/// </summary>
public abstract class WikidataSparqlClient : IMetadataProvider
{
    protected readonly HttpClient HttpClient;
    protected readonly ILogger Logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IProviderLookupCache? _lookupCache;

    private const string SparqlEndpoint = "https://query.wikidata.org/sparql";
    private const string UserAgent = "SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)";

    public abstract LibraryType SupportedType { get; }
    public abstract string ProviderName { get; }

    protected WikidataSparqlClient(HttpClient httpClient, ILogger logger, RateLimiterFactory rateLimiterFactory,
        IProviderLookupCache? lookupCache = null)
    {
        HttpClient = httpClient;
        Logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("Wikidata");
        _lookupCache = lookupCache;
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Subclass opt-out (e.g. ComicWikidataProvider skips non-comic items in a Book library).
        if (!ShouldFetch(item))
            return null;

        // SM-WI-040: a query that definitively missed within the TTL is not re-sent.
        // Subclasses opt in via BuildLookupCacheKey (null = uncacheable, e.g. ID-based).
        var cacheKey = BuildLookupCacheKey(item);
        if (cacheKey != null && _lookupCache != null &&
            await _lookupCache.IsFreshMissAsync(ProviderName, cacheKey))
        {
            Logger.LogDebug("{Provider}: fresh cached miss for '{Title}'; skipping network", ProviderName, item.Title);
            return null;
        }

        try
        {
            var sparqlQuery = BuildSparqlQuery(item);
            var url = $"{SparqlEndpoint}?query={Uri.EscapeDataString(sparqlQuery)}&format=json";

            Logger.LogInformation("Fetching Wikidata {Provider} for {Title}", ProviderName, item.Title);
            var response = await GetStringLimitedAsync(url);

            using var doc = JsonDocument.Parse(response);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");

            if (bindings.GetArrayLength() == 0)
            {
                await RecordMissAsync(cacheKey);
                return null;
            }

            // SM-WI-030: subclasses may disambiguate among multiple bindings (e.g. the
            // movie provider's year check) or reject them all (prefer no metadata over
            // wrong metadata).
            var result = SelectBinding(bindings, item);
            if (result == null)
            {
                await RecordMissAsync(cacheKey); // year-contradiction is definitive for this query
                return null;
            }
            return ExtractMetadata(result.Value, item);
        }
        catch (Exception ex)
        {
            // Transient errors are NOT cached — the retry ladder owns them.
            Logger.LogError(ex, "Error fetching {Provider} metadata for {Title}", ProviderName, item.Title);
            return null;
        }
    }

    private Task RecordMissAsync(string? cacheKey)
        => cacheKey != null && _lookupCache != null
            ? _lookupCache.RecordMissAsync(ProviderName, cacheKey)
            : Task.CompletedTask;

    /// <summary>
    /// SM-WI-040 — key identifying this item's SEARCH query for the negative cache, or
    /// null when the lookup is uncacheable (ID-based, or the subclass opts out).
    /// Must be deterministic across tiers/rescans/amnesty for the same item.
    /// </summary>
    protected virtual string? BuildLookupCacheKey(MediaItem item) => null;

    /// <summary>
    /// Override to short-circuit before any network/rate-limit work when the
    /// provider doesn't apply to this item (e.g. a comic provider receiving an ebook).
    /// Default: always fetch.
    /// </summary>
    protected virtual bool ShouldFetch(MediaItem item) => true;

    /// <summary>
    /// SM-WI-022 — leased GET shared by every Wikidata call site. SPARQL (query.wikidata.org)
    /// and wbsearchentities (www.wikidata.org) run on Wikimedia infrastructure under one
    /// budget here (§2 host mapping sends both to the "Wikidata" limiter). Exactly one
    /// lease per HTTP request; subclass search paths must use this instead of raw
    /// HttpClient calls, which bypassed the limiter entirely.
    /// </summary>
    protected async Task<string> GetStringLimitedAsync(string url, CancellationToken ct = default)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"Wikidata rate-limit queue is full; request rejected locally: {url}");
        }
        return await HttpClient.GetStringAsync(url, ct);
    }

    /// <summary>
    /// Build the SPARQL query for the given media item.
    /// </summary>
    protected abstract string BuildSparqlQuery(MediaItem item);

    /// <summary>
    /// Extract metadata from the selected SPARQL result binding.
    /// </summary>
    protected abstract MetadataResult ExtractMetadata(JsonElement result, MediaItem item);

    /// <summary>
    /// SM-WI-030 — choose which binding to extract, or null to reject them all.
    /// Default: the first (providers whose query is keyed on a unique ID, or that have
    /// no disambiguation signal, keep the old behavior).
    /// </summary>
    protected virtual JsonElement? SelectBinding(JsonElement bindings, MediaItem item) => bindings[0];

    // ─── Shared helpers for subclass use ────────────────────────────────

    /// <summary>
    /// Safely extract a string value from a SPARQL result binding property.
    /// </summary>
    protected static string? GetBindingString(JsonElement result, string property)
    {
        if (result.TryGetProperty(property, out var prop))
        {
            return prop.GetProperty("value").GetString();
        }
        return null;
    }

    /// <summary>
    /// SM-WI-012 — escape a value for embedding in a double-quoted SPARQL literal.
    /// Titles with quotes/backslashes (legal in filenames, NFO titles, admin edits)
    /// otherwise produce malformed SPARQL: HTTP 400 → retry ladder → weekly amnesty,
    /// forever, while burning the WDQS error budget. Public static so every SPARQL
    /// construction site (including non-subclasses like WikidataCollectionResolver)
    /// and tests can use the one implementation.
    /// </summary>
    public static string EscapeForSparql(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Build the standard entity search SPARQL selector for a title and Wikidata class.
    /// The title is escaped here — callers pass it raw. Binds ?ordinal to EntitySearch's
    /// notability rank (SM-WI-030) so multi-candidate queries can ORDER BY it — GROUP BY
    /// does not preserve service result order on its own.
    /// </summary>
    protected static string BuildEntitySearchSelector(string title, string wikidataClass)
    {
        return $@"
            SERVICE wikibase:mwapi {{
                bd:serviceParam wikibase:api ""EntitySearch"" .
                bd:serviceParam wikibase:endpoint ""www.wikidata.org"" .
                bd:serviceParam mwapi:search ""{EscapeForSparql(title)}"" .
                bd:serviceParam mwapi:language ""en"" .
                ?item wikibase:apiOutputItem mwapi:item .
                ?ordinal wikibase:apiOrdinal true .
            }}
            ?item wdt:P31/wdt:P279* wd:{wikidataClass} .
        ";
    }

    /// <summary>
    /// Try to extract a cached entity ID from an item's promoted columns.
    /// </summary>
    protected static string? TryGetCachedId(MediaItem item, string jsonKey, string? expectedPrefix = null)
    {
        // Map known JSON keys to promoted columns
        string? id = jsonKey switch
        {
            "imdbId" => item.ImdbId,
            "tvmazeId" => item.TvMazeId?.ToString(),
            "musicBrainzId" => item.MusicBrainzId,
            _ => null
        };

        if (!string.IsNullOrEmpty(id) && (expectedPrefix == null || id.StartsWith(expectedPrefix)))
            return id;

        return null;
    }
}
