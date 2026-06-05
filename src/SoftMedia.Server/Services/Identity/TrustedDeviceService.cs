using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// Manages "remembered" 2FA devices (P-2FA-expiry). A device that completes 2FA gets a
/// random token (only its hash is stored); on later logins the device may skip the 2FA
/// challenge until <c>Users.TwoFactorExpirationDays</c> elapses since its last 2FA.
/// </summary>
public interface ITrustedDeviceService
{
    /// <summary>
    /// Returns the trusted device for <paramref name="rawToken"/> if it belongs to the
    /// user and its last 2FA is within <paramref name="expirationDays"/>. Returns null
    /// when expiration is disabled (≤0), the token is missing, or the window has elapsed.
    /// </summary>
    Task<TrustedDevice?> FindValidAsync(Guid userId, string? rawToken, int expirationDays, CancellationToken ct = default);

    /// <summary>
    /// Records a successful 2FA for the device. Reuses the existing token if it still maps
    /// to one of the user's devices (refreshing its timestamp); otherwise mints a new one.
    /// Returns the device and the raw token to put in the client cookie.
    /// </summary>
    Task<(TrustedDevice device, string rawToken)> RememberAsync(Guid userId, string? existingRawToken, string? userAgent, string? ip, CancellationToken ct = default);

    Task TouchAsync(TrustedDevice device, CancellationToken ct = default);
    Task<List<TrustedDevice>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<bool> RevokeAsync(Guid userId, Guid deviceId, CancellationToken ct = default);
    Task<int> RevokeAllAsync(Guid userId, CancellationToken ct = default);
}

public class TrustedDeviceService : ITrustedDeviceService
{
    private readonly AppDbContext _db;

    public TrustedDeviceService(AppDbContext db) => _db = db;

    public static string HashToken(string rawToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public async Task<TrustedDevice?> FindValidAsync(Guid userId, string? rawToken, int expirationDays, CancellationToken ct = default)
    {
        if (expirationDays <= 0 || string.IsNullOrEmpty(rawToken)) return null;

        var hash = HashToken(rawToken);
        var device = await _db.TrustedDevices
            .FirstOrDefaultAsync(d => d.UserId == userId && d.TokenHash == hash, ct);
        if (device == null) return null;

        // Window is measured from the last successful 2FA, against the CURRENT setting,
        // so lowering the admin value immediately shortens existing grace.
        if (DateTime.UtcNow - device.LastVerifiedAtUtc >= TimeSpan.FromDays(expirationDays))
            return null;

        return device;
    }

    public async Task<(TrustedDevice device, string rawToken)> RememberAsync(
        Guid userId, string? existingRawToken, string? userAgent, string? ip, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(existingRawToken))
        {
            var hash = HashToken(existingRawToken);
            var existing = await _db.TrustedDevices
                .FirstOrDefaultAsync(d => d.UserId == userId && d.TokenHash == hash, ct);
            if (existing != null)
            {
                existing.LastVerifiedAtUtc = now;
                existing.LastSeenAtUtc = now;
                await _db.SaveChangesAsync(ct);
                return (existing, existingRawToken);
            }
        }

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var device = new TrustedDevice
        {
            UserId = userId,
            TokenHash = HashToken(rawToken),
            CreatedAtUtc = now,
            LastVerifiedAtUtc = now,
            LastSeenAtUtc = now,
            Label = DeriveLabel(userAgent),
            CreatedFromIp = ip,
        };
        _db.TrustedDevices.Add(device);
        await _db.SaveChangesAsync(ct);
        return (device, rawToken);
    }

    public async Task TouchAsync(TrustedDevice device, CancellationToken ct = default)
    {
        device.LastSeenAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<TrustedDevice>> ListAsync(Guid userId, CancellationToken ct = default)
        => _db.TrustedDevices.Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid userId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await _db.TrustedDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
        if (device == null) return false;
        _db.TrustedDevices.Remove(device);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken ct = default)
        => await _db.TrustedDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);

    /// <summary>Best-effort friendly label from a User-Agent (e.g. "Chrome on Windows").</summary>
    private static string? DeriveLabel(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        var ua = userAgent;
        string browser =
            ua.Contains("Edg/") ? "Edge" :
            ua.Contains("OPR/") || ua.Contains("Opera") ? "Opera" :
            ua.Contains("Firefox/") ? "Firefox" :
            ua.Contains("Chrome/") ? "Chrome" :
            ua.Contains("Safari/") ? "Safari" : "Browser";
        string os =
            ua.Contains("Windows") ? "Windows" :
            ua.Contains("Android") ? "Android" :
            ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" :
            ua.Contains("Mac OS") || ua.Contains("Macintosh") ? "macOS" :
            ua.Contains("Linux") ? "Linux" : "device";
        var label = $"{browser} on {os}";
        return label.Length > 80 ? label[..80] : label;
    }
}
