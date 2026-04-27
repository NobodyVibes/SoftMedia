using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

/// Persists refresh tokens server-side as SHA-256 hashes and enforces rotation
/// with reuse detection. A raw token is returned only once — at issue time —
/// and the caller is responsible for handing it to the client (typically as a
/// HttpOnly refresh cookie). Presenting a revoked-and-replaced token is
/// treated as token theft and revokes the entire chain for that user.
public interface IRefreshTokenService
{
    /// Issues a fresh refresh token for <paramref name="user"/>.
    /// Returns the raw token (to set in the response cookie) and the persisted
    /// entity. The hash is persisted; the raw value is never stored.
    Task<(string rawToken, RefreshToken entity)> IssueAsync(
        User user, string? ip, CancellationToken ct = default);

    /// Looks up a token by its hash, checks validity, and reports reuse.
    /// <see cref="RefreshTokenValidationResult.IsReuse"/> is true when the
    /// presented token was previously rotated away — caller should treat this
    /// as theft and call <see cref="RevokeAllForUserAsync"/>.
    Task<RefreshTokenValidationResult> ValidateAsync(
        string rawToken, CancellationToken ct = default);

    /// Revokes <paramref name="current"/> (reason = "rotated"), issues a fresh
    /// token, and links the two via <c>ReplacedByTokenId</c> so future reuse
    /// detection can find the chain.
    Task<(string rawToken, RefreshToken entity)> RotateAsync(
        RefreshToken current, string? ip, CancellationToken ct = default);

    /// Marks a single token revoked. No-op if already revoked.
    Task RevokeAsync(
        RefreshToken token, string reason, string? ip, CancellationToken ct = default);

    /// Revokes every currently-active refresh token for a user. Used on
    /// logout-all, password change, and reuse-detected responses.
    Task RevokeAllForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);
}

public record RefreshTokenValidationResult(
    bool IsValid,
    RefreshToken? Token,
    bool IsReuse)
{
    public static RefreshTokenValidationResult NotFound() => new(false, null, false);
    public static RefreshTokenValidationResult Expired(RefreshToken token) => new(false, token, false);
    public static RefreshTokenValidationResult Revoked(RefreshToken token) => new(false, token, false);
    public static RefreshTokenValidationResult ReuseDetected(RefreshToken token) => new(false, token, true);
    public static RefreshTokenValidationResult Ok(RefreshToken token) => new(true, token, false);
}
