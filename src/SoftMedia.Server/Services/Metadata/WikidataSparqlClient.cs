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

    private const string SparqlEndpoint = "https://query.wikidata.org/sparql";
    private const string UserAgent = "SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)";

    public abstract LibraryType SupportedType { get; }
    public abstract string ProviderName { get; }

    protected WikidataSparqlClient(HttpClient httpClient, ILogger logger, RateLimiterFactory rateLimiterFactory)
    {
        HttpClient = httpClient;
        Logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("Wikidata");
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// Template method: subclasses build the SPARQL query and extract metadata from the result.
    /// </summary>
    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        try
        {
            // Acquire rate limit lease
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                Logger.LogWarning("Wikidata rate limit exceeded for '{Title}', skipping", item.Title);
                return null;
            }

            var sparqlQuery = BuildSparqlQuery(item);
            var url = $"{SparqlEndpoint}?query={Uri.EscapeDataString(sparqlQuery)}&format=json";

            Logger.LogInformation("Fetching Wikidata {Provider} for {Title}", ProviderName, item.Title);
            var response = await HttpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");

            if (bindings.GetArrayLength() == 0)
                return null;

            var result = bindings[0];
            var metadata = ExtractMetadata(result, item);

            return metadata.Count > 0
                ? JsonSerializer.Serialize(metadata)
                : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching {Provider} metadata for {Title}", ProviderName, item.Title);
            return null;
        }
    }

    /// <summary>
    /// Build the SPARQL query for the given media item.
    /// </summary>
    protected abstract string BuildSparqlQuery(MediaItem item);

    /// <summary>
    /// Extract metadata from the first SPARQL result binding into a dictionary.
    /// </summary>
    protected abstract Dictionary<string, object> ExtractMetadata(JsonElement result, MediaItem item);

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
    /// Add a binding value to the metadata dictionary if it exists.
    /// </summary>
    protected static void TryAddBinding(Dictionary<string, object> metadata, JsonElement result, string bindingName, string metadataKey)
    {
        var value = GetBindingString(result, bindingName);
        if (value != null)
            metadata[metadataKey] = value;
    }

    /// <summary>
    /// Add a binding value as a single-element array (e.g., for genres).
    /// </summary>
    protected static void TryAddBindingArray(Dictionary<string, object> metadata, JsonElement result, string bindingName, string metadataKey)
    {
        var value = GetBindingString(result, bindingName);
        if (value != null)
            metadata[metadataKey] = new[] { value };
    }

    /// <summary>
    /// Build the standard entity search SPARQL selector for a title and Wikidata class.
    /// </summary>
    protected static string BuildEntitySearchSelector(string title, string wikidataClass)
    {
        return $@"
            SERVICE wikibase:mwapi {{
                bd:serviceParam wikibase:api ""EntitySearch"" .
                bd:serviceParam wikibase:endpoint ""www.wikidata.org"" .
                bd:serviceParam mwapi:search ""{title}"" .
                bd:serviceParam mwapi:language ""en"" .
                ?item wikibase:apiOutputItem mwapi:item .
            }}
            ?item wdt:P31/wdt:P279* wd:{wikidataClass} .
        ";
    }

    /// <summary>
    /// Try to extract a cached entity ID (e.g., IMDb ID) from an item's MetadataJson.
    /// </summary>
    protected static string? TryGetCachedId(MediaItem item, string jsonKey, string? expectedPrefix = null)
    {
        if (string.IsNullOrEmpty(item.MetadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(item.MetadataJson);
            if (doc.RootElement.TryGetProperty(jsonKey, out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id) && (expectedPrefix == null || id.StartsWith(expectedPrefix)))
                    return id;
            }
        }
        catch { }

        return null;
    }
}
