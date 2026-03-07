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

        // MusicBrainz: 1 request per second (strict limit)
        "MusicBrainz" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 1,
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

        // Default: Conservative 10 requests per 10 seconds
        _ => _limiters.GetOrAdd("default", _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            }))
    };

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
