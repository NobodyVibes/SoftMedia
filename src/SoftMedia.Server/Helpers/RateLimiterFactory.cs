using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Factory for creating and managing rate limiters for external API providers.
/// Uses the built-in System.Threading.RateLimiting for efficient, production-ready rate limiting.
/// </summary>
public class RateLimiterFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();
    private bool _disposed;

    /// <summary>
    /// Gets a rate limiter for the specified provider. Creates one if it doesn't exist.
    /// </summary>
    public RateLimiter GetLimiter(string providerName) => providerName switch
    {
        // TVMaze: 18 requests per 10 seconds (official limit is 20, we use 18 for safety margin)
        "TVMaze" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 18,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 50
            })),

        // MusicBrainz: 1 request per second (strict limit). SM-WI-080 audit finding:
        // with SegmentsPerWindow=1 the "sliding" window degenerates to a fixed window,
        // and two requests could land 60 ms apart across a boundary. 4 segments make
        // the window genuinely slide (adjacent requests ≥~750 ms apart), keeping the
        // observed rate at MB's published 1/s average without boundary bursts.
        "MusicBrainz" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 4,
                PermitLimit = 1,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            })),

        // Wikidata: 5 concurrent requests (using sliding window to emulate)
        "Wikidata" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 30
            })),

        // OpenLibrary: 3 requests per second limit (assuming User-Agent is provided)
        "OpenLibrary" => _limiters.GetOrAdd(providerName, _ =>
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                PermitLimit = 3,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            })),

        // OMDb: Daily limit is real constraint. Throttle modestly to 10 per 10s.
        "OMDb" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100
            })),

        // SM-WI-020: covers.openlibrary.org — OFFICIAL: 100 req/IP per 5 min for
        // non-CoverID/OLID lookups, exceeding = IP block (openlibrary.org/dev/docs/api/covers).
        // 80 leaves a 20% margin for other software on the operator's network.
        "OpenLibraryCovers" => _limiters.GetOrAdd(providerName, _ =>
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(5),
                PermitLimit = 80,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 200
            })),

        // SM-WI-020: coverartarchive.org — no published number (Internet Archive infra);
        // 1/s courtesy pacing, and callers must treat 503 as a throttle signal, never
        // as "art exists" (SM-WI-023). Sliding (4 segments) rather than fixed window —
        // SM-WI-080 audit caught a 682 ms boundary-adjacent pair under fixed windows.
        "CoverArtArchive" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 4,
                PermitLimit = 1,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 30
            })),

        // SM-WI-020: upload.wikimedia.org / commons — Wikimedia UA policy, no hard
        // number published; modest serial access (5/s) is well within expectations.
        "WikimediaImages" => _limiters.GetOrAdd(providerName, _ =>
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                PermitLimit = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100
            })),

        // Default: conservative 10 requests per 10 seconds. Keyed by the REQUESTED name
        // (not a single shared "default") so per-host fallbacks ("host:cdn.example.com")
        // each get their own budget instead of all unknown hosts throttling each other.
        _ => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            }))
    };

    /// <summary>
    /// SM-WI-020 — resolve the limiter for an arbitrary outbound URL by host.
    /// Shared-budget invariant (maintainer, 2026-07-28): every code path that talks to
    /// the same provider host — enrichment, search, image downloads, any library type —
    /// acquires from that provider's ONE limiter instance, so adding queues or channels
    /// can never multiply a provider's request rate. Hosts without a named policy get a
    /// per-host default limiter (one each, not a shared bucket).
    /// </summary>
    public RateLimiter GetLimiterForHost(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var name = host switch
        {
            "covers.openlibrary.org" => "OpenLibraryCovers",
            "openlibrary.org" or "www.openlibrary.org" => "OpenLibrary",
            "coverartarchive.org" or "www.coverartarchive.org" => "CoverArtArchive",
            "upload.wikimedia.org" or "commons.wikimedia.org" => "WikimediaImages",
            "musicbrainz.org" or "www.musicbrainz.org" => "MusicBrainz",
            "api.tvmaze.com" => "TVMaze",
            "omdbapi.com" or "www.omdbapi.com" or "img.omdbapi.com" => "OMDb",
            "query.wikidata.org" or "www.wikidata.org" => "Wikidata",
            _ => "host:" + host,
        };
        return GetLimiter(name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
        _limiters.Clear();
    }
}
