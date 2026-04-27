using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// A server-side refresh-token record. The raw token is never persisted; only
/// the SHA-256 hash. Rotation chains tokens via <see cref="ReplacedByTokenId"/>
/// so that presenting a revoked-and-replaced token can be detected as theft
/// (<see cref="RefreshTokenRevocationReason.ReuseDetected"/>).
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// SHA-256 hash of the raw token, hex-encoded (64 chars).
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// IPv6 textual representation up to 45 chars.
    [MaxLength(45)]
    public string? CreatedByIp { get; set; }

    public DateTime? RevokedAt { get; set; }

    [MaxLength(45)]
    public string? RevokedByIp { get; set; }

    /// Self-FK: when a token is rotated, the old row's ReplacedByTokenId points
    /// to the new row. This lets reuse-detection find the whole chain.
    public Guid? ReplacedByTokenId { get; set; }
    public RefreshToken? ReplacedByToken { get; set; }

    [MaxLength(32)]
    public string? ReasonRevoked { get; set; }

    public bool IsActive => RevokedAt == null && DateTime.UtcNow < ExpiresAt;
}

public static class RefreshTokenRevocationReason
{
    public const string Rotated = "rotated";
    public const string Logout = "logout";
    public const string PasswordChange = "password-change";
    public const string ReuseDetected = "reuse-detected";

    /// The account was banned, deleted, or unapproved between token issue and
    /// refresh — the token is still cryptographically valid but no longer
    /// usable. Distinct from <see cref="ReuseDetected"/> so forensic logs
    /// don't claim theft when the real cause is an admin action.
    public const string AccountSuspended = "account-suspended";
}
