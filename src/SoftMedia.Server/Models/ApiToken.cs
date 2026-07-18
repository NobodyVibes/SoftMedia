using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// A long-lived, user-scoped programmatic credential (for dashboards, Home
/// Assistant, companion tools). The raw token is shown to the user exactly once
/// on mint and never persisted — only its SHA-256 hash is stored, mirroring
/// <see cref="RefreshToken"/>. Unlike refresh tokens these do not rotate; they
/// live until revoked or expired.
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// SHA-256 hash of the raw token, hex-encoded (64 chars).
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Label { get; set; } = string.Empty;

    /// JSON array of scope strings (see <see cref="ApiTokenScopes"/>).
    public string Scopes { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    [MaxLength(45)]
    public string? LastUsedIp { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// Null = never expires.
    public DateTime? ExpiresAt { get; set; }

    public bool IsActive => RevokedAt == null && (ExpiresAt == null || DateTime.UtcNow < ExpiresAt);
}

/// <summary>
/// The coarse v1 scope vocabulary. Deliberately small — resist fine-grained
/// scopes until a concrete integration needs them.
/// </summary>
public static class ApiTokenScopes
{
    public const string ReadLibrary = "read:library";
    public const string ReadState = "read:state";
    public const string WriteState = "write:state";
    /// <summary>
    /// R-WI-019 — library mutation triggers (scan webhook). Deliberately narrow so a
    /// Sonarr/Radarr config holds a least-privilege credential, not a full-admin one.
    /// </summary>
    public const string WriteLibrary = "write:library";
    public const string Admin = "admin";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { ReadLibrary, ReadState, WriteState, WriteLibrary, Admin };

    public static bool IsValid(string scope) => All.Contains(scope);
}
