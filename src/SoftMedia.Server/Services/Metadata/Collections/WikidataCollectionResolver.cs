using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Metadata.Collections;

/// <summary>
/// Wave E2 — bridges OMDb-sourced movies to Wikidata's <c>wdt:P179</c> series
/// graph. OMDb has no franchise / collection field at all (verified from the
/// OMDbProvider parser, lines 352-469); the canonical answer for SoftMedia is
/// to use the IMDb ID OMDb already returns and look up the parent series in
/// Wikidata via <c>wdt:P345</c> (IMDb ID → Wikidata QID) → <c>wdt:P179</c>
/// (part of the series).
///
/// One SPARQL call per resolved movie. Result is then cached on the calling
/// item via <c>MediaItem.CollectionLookupAttempted</c> so a re-scan never
/// retries. Wikidata is keyless and rate-limited by the existing
/// <see cref="RateLimiterFactory"/>; we acquire one slot per call.
///
/// Uses its own typed <see cref="HttpClient"/> registered alongside the
/// existing Wikidata providers so the SDD §4.3 User-Agent header is set
/// uniformly via <c>SoftMediaUserAgentHandler</c>.
/// </summary>
public class WikidataCollectionResolver
{
    private const string SparqlEndpoint = "https://query.wikidata.org/sparql";

    private readonly HttpClient _httpClient;
    private readonly ILogger<WikidataCollectionResolver> _logger;
    private readonly RateLimiter _rateLimiter;

    public WikidataCollectionResolver(
        HttpClient httpClient,
        ILogger<WikidataCollectionResolver> logger,
        RateLimiterFactory rateLimiterFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("Wikidata");
    }

    /// <summary>
    /// Resolves the parent collection / series for a movie identified by its
    /// IMDb ID. Returns null if the movie is in no series, the IMDb ID is
    /// unknown to Wikidata, or the network call fails.
    /// </summary>
    public virtual async Task<CollectionLookupResult?> ResolveByImdbIdAsync(
        string imdbId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // SPARQL injection avoidance: imdbId is `^tt[0-9]+$` per the OMDb
        // contract; we double-check the prefix above. The body of the literal
        // is wrapped in quotes inside the query — any non-conforming value
        // would have failed the prefix check.
        var sparqlQuery = $@"
            SELECT ?series ?seriesLabel ?seriesPoster WHERE {{
                ?film wdt:P345 ""{imdbId}"" .
                ?film wdt:P179 ?series .
                OPTIONAL {{ ?series wdt:P18 ?seriesPoster . }}
                SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"" . }}
            }} LIMIT 1";

        try
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("[CollectionResolver] Wikidata rate limit exceeded for {ImdbId}", imdbId);
                return null;
            }

            var url = $"{SparqlEndpoint}?query={Uri.EscapeDataString(sparqlQuery)}&format=json";
            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(response);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() == 0)
            {
                return null;
            }

            var first = bindings[0];
            var seriesUri = first.TryGetProperty("series", out var seriesProp)
                ? seriesProp.GetProperty("value").GetString()
                : null;
            var seriesLabel = first.TryGetProperty("seriesLabel", out var labelProp)
                ? labelProp.GetProperty("value").GetString()
                : null;
            var posterUri = first.TryGetProperty("seriesPoster", out var posterProp)
                ? posterProp.GetProperty("value").GetString()
                : null;

            if (string.IsNullOrEmpty(seriesUri) || string.IsNullOrEmpty(seriesLabel))
            {
                return null;
            }

            // Series URI is "http://www.wikidata.org/entity/Q12345"; we want the trailing QID.
            var qid = seriesUri.Substring(seriesUri.LastIndexOf('/') + 1);
            if (string.IsNullOrEmpty(qid) || !qid.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _logger.LogInformation(
                "[CollectionResolver] Resolved {ImdbId} → series {Qid} ({Label})",
                imdbId, qid, seriesLabel);

            return new CollectionLookupResult(qid, seriesLabel, posterUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CollectionResolver] Lookup failed for {ImdbId}", imdbId);
            return null;
        }
    }
}

public record CollectionLookupResult(string WikidataId, string Name, string? PosterUrl);
