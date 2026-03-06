using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// A DelegatingHandler that applies rate limiting to outgoing HTTP requests using a provided RateLimiter.
/// </summary>
public class RateLimitingDelegatingHandler : DelegatingHandler
{
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<RateLimitingDelegatingHandler> _logger;

    public RateLimitingDelegatingHandler(RateLimiter rateLimiter, ILogger<RateLimitingDelegatingHandler> logger)
    {
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Attempt to acquire a lease.
        // We wait effectively indefinitely (or until cancelled) because if we are hitting the rate limit,
        // we want to queue up and wait rather than fail immediately.
        // The RateLimiter options (QueueLimit) determine when we inevitably fail if too many are queued.
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);

        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // If lease failed (e.g. queue full), we return 429 or 503 equivalent exception or response.
        // Throwing an exception is often better for Polly retries further up, but returning 429 is semantic.
        // Given this is a client, we should probably throw so the caller knows it failed locally.
        _logger.LogWarning("Rate limit exceeded for {Url}. Queue limit reached.", request.RequestUri);
        throw new InvalidOperationException($"Rate limit exceeded for {request.RequestUri}. Request rejected by local rate limiter.");
    }
}
