namespace SoftMedia.Server.Models;

/// <summary>
/// SM-WI-040 — negative-result memory for provider searches. One row per
/// (Provider, QueryKey) that returned a DEFINITIVE "no match": while fresh (30-day TTL),
/// the provider short-circuits without any network call, so never-matching items stop
/// re-running identical searches on every retry tier, rescan, and weekly amnesty.
/// Deliberately NOT recorded for transient errors — those must keep riding the
/// 1m/5m/30m/4h retry ladder, which exists exactly for them. ID-based lookups
/// (ImdbId/TvMazeId/MBID/OpenLibraryKey/ISBN-column) are never cached either: a stored
/// id is already authoritative. Rows are keyed by query, not item — stale rows past TTL
/// are simply ignored (a matched item never searches again, so no cleanup pressure).
/// </summary>
public class ProviderLookupCacheEntry
{
    /// <summary>Provider name (composite key with <see cref="QueryKey"/>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Normalized query ("type|title|year" style, lowercase, trimmed).</summary>
    public string QueryKey { get; set; } = string.Empty;

    public DateTime LastAttemptUtc { get; set; }

    /// <summary>How many times this exact query has been attempted and missed.</summary>
    public int AttemptCount { get; set; }
}
