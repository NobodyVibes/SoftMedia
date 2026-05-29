using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// Per-user TOTP (RFC 6238) enrollment for two-factor auth (P2-WI-005). One row per
/// user (UserId is the PK). The shared secret is stored AES-encrypted; recovery codes
/// are stored only as SHA-256 hashes and are single-use.
/// </summary>
public class UserTotp
{
    [Key]
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// Base64 of [IV ‖ ciphertext]; the Base32 TOTP secret encrypted with an AES key
    /// derived from the server's JWT signing secret. See TotpService.
    public string EncryptedSecret { get; set; } = string.Empty;

    /// Null until the user confirms a code during enrollment; non-null = 2FA active.
    public DateTime? EnabledAt { get; set; }

    /// JSON array of SHA-256 hashes of one-time recovery codes (still valid).
    public string RecoveryCodes { get; set; } = "[]";

    /// JSON array of SHA-256 hashes of recovery codes already consumed.
    public string UsedRecoveryCodes { get; set; } = "[]";

    public bool IsEnabled => EnabledAt != null;
}
