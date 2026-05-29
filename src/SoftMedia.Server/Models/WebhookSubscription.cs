using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// A user-configured outbound webhook (P2-WI-004). On a matching event, SoftMedia
/// POSTs a signed JSON payload to <see cref="Url"/>. The user owns the URL — there is
/// no first-party relay, consistent with the privacy charter.
/// </summary>
public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required, MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    /// JSON array of event names this subscription wants (see <see cref="WebhookEvents"/>).
    public string Events { get; set; } = "[]";

    /// Per-subscription HMAC-SHA256 secret. The recipient verifies the
    /// X-SoftMedia-Signature header against the raw body using this value.
    [MaxLength(128)]
    public string Secret { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastDeliveryAt { get; set; }

    /// Last delivery outcome — HTTP status code as text, or an error string.
    [MaxLength(200)]
    public string? LastDeliveryStatus { get; set; }
}

/// <summary>
/// Event taxonomy. v1 ships only the events with clean server-side hook points
/// (P2-WI-004 rescope); media.added / media.played / transcode.failed are deferred
/// to a follow-up because they require new signals that don't exist yet.
/// </summary>
public static class WebhookEvents
{
    public const string LibraryScanCompleted = "library.scan.completed";
    public const string LibraryScanFailed = "library.scan.failed";
    public const string Test = "webhook.test";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { LibraryScanCompleted, LibraryScanFailed, Test };

    public static bool IsValid(string e) => All.Contains(e);
}
