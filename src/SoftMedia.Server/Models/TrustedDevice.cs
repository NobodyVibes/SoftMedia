using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// A browser/device that has completed 2FA for a user and may skip the 2FA challenge
/// until the configured expiration window elapses (Users.TwoFactorExpirationDays).
/// The device is identified by a random token; only its SHA-256 hash is stored. Rows
/// are revocable individually or in bulk, and are cleared when 2FA is disabled.
/// </summary>
[Index(nameof(TokenHash))]
[Index(nameof(UserId))]
public class TrustedDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    /// <summary>SHA-256 (hex) of the opaque device token held by the client cookie.</summary>
    [Required]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last successful 2FA on this device — the expiration window is measured from here.</summary>
    public DateTime LastVerifiedAtUtc { get; set; }

    /// <summary>Last login that this device let through without a 2FA prompt.</summary>
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>Human-friendly label derived from the User-Agent at enrolment.</summary>
    public string? Label { get; set; }

    public string? CreatedFromIp { get; set; }
}
