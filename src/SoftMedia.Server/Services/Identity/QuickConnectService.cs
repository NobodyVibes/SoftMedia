using System.Security.Cryptography;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// NR-WI-006 — Quick Connect device pairing. A device that can't comfortably take a
/// password + TOTP (TV, phone app) calls Initiate to get a short human-readable code,
/// shows it to the user, and polls with its private secret. The user types the code
/// into their ALREADY-AUTHENTICATED web session to approve; the device's next poll
/// claims tokens for that user. All state is in-memory and short-lived by design —
/// a restart simply voids pending pairings.
/// </summary>
public interface IQuickConnectService
{
    /// <summary>Starts a pairing. Returns null when the pending store is full (DoS bound).</summary>
    QuickConnectInitiation? Initiate(string? deviceName, string? requestIp);

    /// <summary>Pending-entry details for the approval UI. Null when unknown/expired/already approved.</summary>
    QuickConnectPending? PeekPending(string code);

    /// <summary>Approves a pending code, binding it to <paramref name="userId"/>. False when unknown/expired/already approved.</summary>
    bool Authorize(string code, Guid userId);

    /// <summary>
    /// Polled by the device. Approved entries are consumed EXACTLY once — the second
    /// claim of the same secret reports NotFound, so a leaked secret can't mint a
    /// second session after the device has its tokens.
    /// </summary>
    QuickConnectClaim TryClaim(string secret);
}

public record QuickConnectInitiation(string Code, string Secret, int ExpiresInSeconds);
public record QuickConnectPending(string Code, string? DeviceName, string? RequestIp, DateTime CreatedAt);
public record QuickConnectClaim(QuickConnectClaimStatus Status, Guid? UserId = null);
public enum QuickConnectClaimStatus { NotFound, Pending, Approved }

public class QuickConnectService : IQuickConnectService
{
    // No I/O/0/1 — codes are read off a TV screen and typed by a human.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    // Bound on concurrently-pending pairings: initiate is anonymous (rate-limited per IP),
    // so the store must have a hard cap a distributed attacker can't blow past.
    private const int MaxPending = 100;

    private sealed class Entry
    {
        public required string Code;
        public required string Secret;
        public string? DeviceName;
        public string? RequestIp;
        public DateTime CreatedAt;
        public DateTime ExpiresAt;
        public Guid? ApprovedUserId;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _bySecret = new(StringComparer.Ordinal);
    private readonly ILogger<QuickConnectService> _logger;

    public QuickConnectService(ILogger<QuickConnectService> logger)
    {
        _logger = logger;
    }

    public QuickConnectInitiation? Initiate(string? deviceName, string? requestIp)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            if (_byCode.Count >= MaxPending)
            {
                _logger.LogWarning("Quick Connect initiate rejected: pending store full ({Count})", _byCode.Count);
                return null;
            }

            // Regenerate on the (rare) code collision among pending entries.
            string code;
            do { code = GenerateCode(); } while (_byCode.ContainsKey(code));

            var entry = new Entry
            {
                Code = code,
                Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                DeviceName = Truncate(deviceName, 64),
                RequestIp = requestIp,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow + Ttl,
            };
            _byCode[code] = entry;
            _bySecret[entry.Secret] = entry;
            return new QuickConnectInitiation(code, entry.Secret, (int)Ttl.TotalSeconds);
        }
    }

    public QuickConnectPending? PeekPending(string code)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            if (!_byCode.TryGetValue(code, out var e) || e.ApprovedUserId != null) return null;
            return new QuickConnectPending(e.Code, e.DeviceName, e.RequestIp, e.CreatedAt);
        }
    }

    public bool Authorize(string code, Guid userId)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            if (!_byCode.TryGetValue(code, out var e) || e.ApprovedUserId != null) return false;
            e.ApprovedUserId = userId;
            return true;
        }
    }

    public QuickConnectClaim TryClaim(string secret)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            if (!_bySecret.TryGetValue(secret, out var e))
                return new QuickConnectClaim(QuickConnectClaimStatus.NotFound);

            if (e.ApprovedUserId is null)
                return new QuickConnectClaim(QuickConnectClaimStatus.Pending);

            // Single-use: consume on the successful claim.
            RemoveLocked(e);
            return new QuickConnectClaim(QuickConnectClaimStatus.Approved, e.ApprovedUserId);
        }
    }

    private void PruneExpiredLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var e in _byCode.Values.Where(e => e.ExpiresAt <= now).ToList())
        {
            RemoveLocked(e);
        }
    }

    private void RemoveLocked(Entry e)
    {
        _byCode.Remove(e.Code);
        _bySecret.Remove(e.Secret);
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }
        return new string(chars);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
