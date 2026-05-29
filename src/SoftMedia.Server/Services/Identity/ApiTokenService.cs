using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Identity;

public record ApiTokenAuthResult(User User, IReadOnlyList<string> Scopes, ApiToken Token);

public interface IApiTokenService
{
    /// The raw-token prefix. Recognisable to secret scanners (e.g. GitHub) so a
    /// leaked token can be detected automatically.
    const string Prefix = "sm_";

    /// <summary>
    /// Mints a token for the user. Returns the RAW token (shown once, never stored)
    /// and the persisted record. Throws <see cref="InvalidOperationException"/> if a
    /// non-admin requests the admin scope, or if any scope is unknown.
    /// </summary>
    Task<(string rawToken, ApiToken entity)> CreateAsync(
        Guid userId, string label, IReadOnlyList<string> scopes, DateTime? expiresAt, CancellationToken ct);

    Task<IReadOnlyList<ApiToken>> ListAsync(Guid userId, CancellationToken ct);

    /// Revokes a token the caller owns. Returns false if not found / not owned.
    Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken ct);

    /// <summary>
    /// Resolves a raw "sm_*" token to its owner + scopes, updating LastUsed.
    /// Returns null if the token is unknown, revoked, expired, or the user is not
    /// in good standing. Safe to call with any string (non-sm_ returns null fast).
    /// </summary>
    Task<ApiTokenAuthResult?> AuthenticateAsync(string rawToken, string? ip, CancellationToken ct);
}

public class ApiTokenService : IApiTokenService
{
    private readonly AppDbContext _db;

    public ApiTokenService(AppDbContext db) => _db = db;

    public async Task<(string rawToken, ApiToken entity)> CreateAsync(
        Guid userId, string label, IReadOnlyList<string> scopes, DateTime? expiresAt, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("User not found.");

        var requested = (scopes ?? Array.Empty<string>()).Distinct().ToList();
        if (requested.Count == 0)
            throw new InvalidOperationException("At least one scope is required.");

        foreach (var s in requested)
            if (!ApiTokenScopes.IsValid(s))
                throw new InvalidOperationException($"Unknown scope '{s}'.");

        // Admin scope is mintable only by admins — enforced here, independent of UI.
        if (requested.Contains(ApiTokenScopes.Admin) && user.Role != UserRole.Admin)
            throw new InvalidOperationException("Only admins may mint an admin-scoped token.");

        var raw = GenerateRawToken();
        var entity = new ApiToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            Label = string.IsNullOrWhiteSpace(label) ? "Unnamed token" : label.Trim(),
            Scopes = JsonSerializer.Serialize(requested),
            ExpiresAt = expiresAt,
        };
        _db.ApiTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return (raw, entity);
    }

    public async Task<IReadOnlyList<ApiToken>> ListAsync(Guid userId, CancellationToken ct)
        => await _db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken ct)
    {
        var token = await _db.ApiTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct);
        if (token == null || token.RevokedAt != null) return false;
        token.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ApiTokenAuthResult?> AuthenticateAsync(string rawToken, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(IApiTokenService.Prefix, StringComparison.Ordinal))
            return null;

        var hash = Hash(rawToken);
        var token = await _db.ApiTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token == null || !token.IsActive) return null;

        var user = token.User;
        if (user == null || user.IsBanned || user.IsDeleted || !user.IsApproved) return null;

        // Best-effort last-used tracking; never block auth on the write.
        token.LastUsedAt = DateTime.UtcNow;
        token.LastUsedIp = ip;
        try { await _db.SaveChangesAsync(ct); } catch { /* ignore */ }

        var scopes = JsonSerializer.Deserialize<List<string>>(token.Scopes) ?? new List<string>();
        return new ApiTokenAuthResult(user, scopes, token);
    }

    // ~40 base32 chars of entropy. Base32 (Crockford-free RFC4648 alphabet via
    // Base64Url then strip) is unnecessary complexity; use Base64Url which is
    // URL/header-safe and avoids +,/,= the way RefreshTokenService does.
    private static string GenerateRawToken()
        => IApiTokenService.Prefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(30));

    private static string Hash(string raw)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
