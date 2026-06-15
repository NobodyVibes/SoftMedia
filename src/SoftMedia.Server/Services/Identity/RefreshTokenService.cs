using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Identity;

public class RefreshTokenService : IRefreshTokenService
{
    public const int RawTokenByteLength = 64;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public RefreshTokenService(AppDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<(string rawToken, RefreshToken entity)> IssueAsync(
        User user, string? ip, CancellationToken ct = default)
    {
        var raw = GenerateRawToken();
        var now = _time.GetUtcNow().UtcDateTime;

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + DefaultLifetime,
            CreatedByIp = Truncate(ip, 45),
        };

        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return (raw, entity);
    }

    public async Task<RefreshTokenValidationResult> ValidateAsync(
        string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return RefreshTokenValidationResult.NotFound();
        }

        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct);

        if (token is null)
        {
            return RefreshTokenValidationResult.NotFound();
        }

        // Reuse detection: a token that was revoked AND replaced has been
        // rotated away. Presenting it again means either the client failed to
        // update its cookie (benign) or someone else is holding the old value
        // (theft). Either way, safest response is to invalidate the chain.
        if (token.RevokedAt != null && token.ReplacedByTokenId != null)
        {
            return RefreshTokenValidationResult.ReuseDetected(token);
        }

        if (token.RevokedAt != null)
        {
            return RefreshTokenValidationResult.Revoked(token);
        }

        if (_time.GetUtcNow().UtcDateTime >= token.ExpiresAt)
        {
            return RefreshTokenValidationResult.Expired(token);
        }

        return RefreshTokenValidationResult.Ok(token);
    }

    public async Task<(string rawToken, RefreshToken entity)?> RotateAsync(
        RefreshToken current, string? ip, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var raw = GenerateRawToken();

        var replacement = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = current.UserId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + DefaultLifetime,
            CreatedByIp = Truncate(ip, 45),
        };

        // Audit wave-2 I-5: atomically CLAIM the current token (flip RevokedAt null->now) so two
        // concurrent refreshes of the same token can't each create a replacement and fork the chain
        // (which would silently defeat reuse-detection). Only the claim that flips RevokedAt
        // proceeds; the loser returns null.
        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            // The InMemory provider (tests) doesn't support ExecuteUpdate and doesn't enforce FKs;
            // tests are single-threaded so a tracked check-and-set is sufficient there.
            if (current.RevokedAt != null) return null;
            current.RevokedAt = now;
            current.RevokedByIp = Truncate(ip, 45);
            current.ReasonRevoked = RefreshTokenRevocationReason.Rotated;
            current.ReplacedByTokenId = replacement.Id;
            _db.RefreshTokens.Add(replacement);
            await _db.SaveChangesAsync(ct);
            return (raw, replacement);
        }

        // Real provider: INSERT the replacement first so the ReplacedByTokenId FK target exists,
        // THEN atomically claim `current` via a conditional UPDATE ... WHERE RevokedAt IS NULL.
        // (Setting ReplacedByTokenId before the replacement row exists violates the self-FK.)
        _db.RefreshTokens.Add(replacement);
        await _db.SaveChangesAsync(ct);

        var affected = await _db.RefreshTokens
            .Where(rt => rt.Id == current.Id && rt.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(rt => rt.RevokedAt, now)
                .SetProperty(rt => rt.RevokedByIp, Truncate(ip, 45))
                .SetProperty(rt => rt.ReasonRevoked, RefreshTokenRevocationReason.Rotated)
                .SetProperty(rt => rt.ReplacedByTokenId, replacement.Id), ct);

        if (affected == 1) return (raw, replacement);

        // Lost the race — another concurrent refresh already rotated `current`. Remove the orphan
        // replacement we inserted (its raw token was never handed out) so the chain doesn't fork.
        _db.RefreshTokens.Remove(replacement);
        await _db.SaveChangesAsync(ct);
        return null;
    }

    public async Task RevokeAsync(
        RefreshToken token, string reason, string? ip, CancellationToken ct = default)
    {
        if (token.RevokedAt != null)
        {
            return;
        }

        token.RevokedAt = _time.GetUtcNow().UtcDateTime;
        token.RevokedByIp = Truncate(ip, 45);
        token.ReasonRevoked = reason;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var active = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in active)
        {
            token.RevokedAt = now;
            token.ReasonRevoked = reason;
        }

        if (active.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        return Base64UrlEncoder.Encode(bytes);
    }

    private static string Hash(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
