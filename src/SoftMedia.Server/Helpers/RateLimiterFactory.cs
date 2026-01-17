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

        // Wikidata: 10 requests per 10 seconds (conservative estimate)
        "Wikidata" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 30
            })),

        // OMDb: 20 requests per 10 seconds (confirmed by testing)
        "OMDb" => _limiters.GetOrAdd(providerName, _ =>
            new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 20,
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
