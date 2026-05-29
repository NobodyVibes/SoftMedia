using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>An event to fan out to all subscriptions that requested it.</summary>
public record WebhookEvent(string EventName, object Payload, Guid? ActorUserId = null, string? ActorUsername = null);

/// <summary>
/// Enqueues webhook events (P2-WI-004). Producers call <see cref="Enqueue"/>; the
/// background worker drains the queue and POSTs to matching subscriptions. Singleton
/// so the in-memory queue is shared process-wide.
/// </summary>
public interface IWebhookDispatcher
{
    void Enqueue(WebhookEvent evt);
    bool TryDequeue(out WebhookEvent evt);
    int QueueDepth { get; }
}

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly ConcurrentQueue<WebhookEvent> _queue = new();
    public void Enqueue(WebhookEvent evt) => _queue.Enqueue(evt);
    public bool TryDequeue(out WebhookEvent evt) => _queue.TryDequeue(out evt!);
    public int QueueDepth => _queue.Count;
}

/// <summary>
/// Stateless helpers for signing and SSRF-validating a webhook delivery. Separated
/// from the queue so they can be unit-tested without a worker.
/// </summary>
public static class WebhookSecurity
{
    /// HMAC-SHA256 of the raw body, hex, prefixed "sha256=" (GitHub-style).
    public static string Sign(string secret, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = h.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(sig).ToLowerInvariant();
    }

    /// <summary>
    /// Validates a target URL against SSRF policy. Returns null if allowed, or a reason
    /// string if rejected. <paramref name="resolvedIps"/> are the DNS-resolved addresses
    /// of the host (caller resolves; this keeps the method synchronous + testable).
    /// </summary>
    public static string? ValidateTarget(Uri uri, IReadOnlyList<IPAddress> resolvedIps, bool allowHttp, bool allowLoopback)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "Only http(s) URLs are allowed.";

        var anyLoopback = resolvedIps.Any(NetworkClassifier.IsLoopback);
        var anyPrivate = resolvedIps.Any(NetworkClassifier.IsPrivate);
        var anyPublic = resolvedIps.Any(ip => !NetworkClassifier.IsLan(ip));

        if (anyLoopback && !allowLoopback)
            return "Loopback webhook targets are disabled.";

        // HTTP (non-TLS) is only permitted to private/loopback targets unless explicitly allowed.
        if (uri.Scheme == Uri.UriSchemeHttp && anyPublic && !allowHttp)
            return "Plain-HTTP webhooks to public hosts are disabled; use HTTPS.";

        // Reject DNS-rebinding-style mixes (a host that resolves to both public and private).
        if (anyPublic && (anyPrivate || anyLoopback))
            return "Webhook host resolves to both public and internal addresses.";

        return null;
    }
}
