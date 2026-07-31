using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// SM-WI-020 — a DelegatingHandler that rate-limits outgoing HTTP requests PER HOST via
/// <see cref="RateLimiterFactory.GetLimiterForHost"/>. Replaces the old single-limiter
/// variant that pushed every image host through the borrowed TVMaze limiter — which both
/// over-throttled fast hosts and could exceed covers.openlibrary.org's official
/// 100 req/5 min cap (18/10s ≈ 540/5 min). One limiter instance per host, shared with
/// every other code path that talks to that host (shared-budget invariant, §2 of the
/// scan-metadata remediation plan).
/// </summary>
public class RateLimitingDelegatingHandler : DelegatingHandler
{
    private readonly RateLimiterFactory _limiterFactory;
    private readonly ILogger<RateLimitingDelegatingHandler> _logger;

    public RateLimitingDelegatingHandler(RateLimiterFactory limiterFactory, ILogger<RateLimitingDelegatingHandler> logger)
    {
        _limiterFactory = limiterFactory ?? throw new ArgumentNullException(nameof(limiterFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Queue up and wait rather than fail immediately; the limiter's QueueLimit
        // bounds how many waiters can pile up before requests are rejected.
        var limiter = _limiterFactory.GetLimiterForHost(request.RequestUri);
        using var lease = await limiter.AcquireAsync(permitCount: 1, cancellationToken);

        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        _logger.LogWarning("Rate limit queue full for host {Host} ({Url}); request rejected locally.",
            request.RequestUri.Host, request.RequestUri);
        throw new InvalidOperationException(
            $"Rate limit exceeded for {request.RequestUri}. Request rejected by local rate limiter.");
    }
}
