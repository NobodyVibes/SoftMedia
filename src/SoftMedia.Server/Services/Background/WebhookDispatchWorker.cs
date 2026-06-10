using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Background;

/// <summary>
/// Drains the webhook queue (P2-WI-004) and POSTs signed payloads to matching, active
/// subscriptions. Retries with exponential backoff; on final failure records a delivery
/// status and dead-letters an admin SystemNotification. AppDbContext is scoped, so each
/// drain cycle creates a scope.
/// </summary>
public class WebhookDispatchWorker : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryBackoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30) };

    private readonly IWebhookDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatchWorker> _logger;

    public WebhookDispatchWorker(
        IWebhookDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatchWorker> logger)
    {
        _dispatcher = dispatcher;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_dispatcher.TryDequeue(out var evt))
            {
                try { await Task.Delay(IdlePoll, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try { await DispatchAsync(evt, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Webhook dispatch failed for {Event}", evt.EventName); }
        }
    }

    private async Task DispatchAsync(WebhookEvent evt, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (!await settings.GetSettingAsync("Webhooks.Enabled", true)) return;

        var allowHttp = await settings.GetSettingAsync("Webhooks.AllowHttp", false);
        var allowLoopback = await settings.GetSettingAsync("Webhooks.AllowLoopback", false);
        // SSRF (audit M5): private/link-local targets are blocked unless the operator opts in.
        var allowPrivate = await settings.GetSettingAsync("Webhooks.AllowPrivateNetwork", false);
        var timeoutSec = await settings.GetSettingAsync("Webhooks.RequestTimeoutSeconds", 10);

        var subs = await db.WebhookSubscriptions.Where(w => w.Active).ToListAsync(ct);
        var matching = subs.Where(s =>
        {
            var events = JsonSerializer.Deserialize<List<string>>(s.Events) ?? new();
            return events.Contains(evt.EventName);
        }).ToList();
        if (matching.Count == 0) return;

        var body = JsonSerializer.Serialize(new
        {
            @event = evt.EventName,
            timestamp = DateTime.UtcNow,
            actor = evt.ActorUserId == null ? null : new { userId = evt.ActorUserId, username = evt.ActorUsername },
            payload = evt.Payload,
        });

        foreach (var sub in matching)
            await DeliverAsync(db, sub, evt.EventName, body, allowHttp, allowLoopback, allowPrivate, timeoutSec, ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task DeliverAsync(AppDbContext db, WebhookSubscription sub, string eventName, string body,
        bool allowHttp, bool allowLoopback, bool allowPrivate, int timeoutSec, CancellationToken ct)
    {
        sub.LastDeliveryAt = DateTime.UtcNow;

        // SSRF guard: parse + DNS-resolve + classify before sending.
        if (!Uri.TryCreate(sub.Url, UriKind.Absolute, out var uri))
        {
            sub.LastDeliveryStatus = "invalid URL";
            return;
        }
        IReadOnlyList<IPAddress> ips;
        try { ips = (await Dns.GetHostAddressesAsync(uri.Host, ct)).ToList(); }
        catch { sub.LastDeliveryStatus = "DNS resolution failed"; return; }
        if (ips.Count == 0) { sub.LastDeliveryStatus = "DNS resolution failed"; return; }

        var rejection = WebhookSecurity.ValidateTarget(uri, ips, allowHttp, allowLoopback, allowPrivate);
        if (rejection != null) { sub.LastDeliveryStatus = "blocked: " + rejection; return; }

        // Pin to a validated address so the actual connection cannot be rebound to an internal
        // host between validation and send (audit M6). All ips passed ValidateTarget collectively,
        // so any is safe; the ConnectCallback on the "Webhooks" client honours this option.
        var pinnedIp = ips[0];

        var signature = WebhookSecurity.Sign(sub.Secret, body);
        var client = _httpClientFactory.CreateClient("Webhooks");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 60));

        for (var attempt = 0; attempt <= RetryBackoff.Length; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Options.Set(WebhookSecurity.PinnedIpOption, pinnedIp);
                req.Headers.TryAddWithoutValidation("X-SoftMedia-Event", eventName);
                req.Headers.TryAddWithoutValidation("X-SoftMedia-Signature", signature);
                req.Headers.TryAddWithoutValidation("User-Agent", "SoftMedia-Webhooks/1.0");

                using var resp = await client.SendAsync(req, ct);
                var code = (int)resp.StatusCode;

                // SSRF defense: the "Webhooks" client has AllowAutoRedirect=false. A 3xx
                // would point at a Location we have NOT SSRF-validated (it could resolve
                // to 169.254.169.254 / 127.0.0.1 / RFC1918), so we never follow it — we
                // treat it as a permanent, non-retryable block. Receivers should expose a
                // stable endpoint, not a redirector.
                if (code >= 300 && code < 400)
                {
                    sub.LastDeliveryStatus = $"blocked: redirect ({code}) not followed";
                    return;
                }

                sub.LastDeliveryStatus = code.ToString();
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                sub.LastDeliveryStatus = "error: " + ex.GetType().Name;
            }

            if (attempt < RetryBackoff.Length)
            {
                try { await Task.Delay(RetryBackoff[attempt], ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        // All attempts failed → dead-letter an admin notification.
        var notifications = db; // same context
        notifications.SystemNotifications.Add(new SystemNotification
        {
            Type = "webhook_failed",
            Title = "Webhook delivery failed",
            Message = $"Delivery of '{eventName}' to {uri.Host} failed after retries (last status: {sub.LastDeliveryStatus}).",
            Severity = "warning",
            Metadata = JsonSerializer.Serialize(new { subscriptionId = sub.Id, eventName }),
        });
        _logger.LogWarning("Webhook {Sub} to {Host} dead-lettered after retries", sub.Id, uri.Host);
    }
}
