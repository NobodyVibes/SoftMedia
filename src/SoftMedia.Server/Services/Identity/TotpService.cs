using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OtpNet;

namespace SoftMedia.Server.Services.Identity;

public record TotpEnrollment(string Secret, string OtpAuthUri);

public interface ITotpService
{
    /// Generates a fresh Base32 secret + otpauth:// URI for QR rendering (client-side).
    TotpEnrollment CreateEnrollment(string username);

    /// Verifies a 6-digit code against the (encrypted) secret, with a ±1 step window.
    bool VerifyCode(string encryptedSecret, string code);

    /// AES-encrypts a Base32 secret for storage; decrypt is internal to VerifyCode.
    string EncryptSecret(string base32Secret);

    /// Generates N human-friendly recovery codes (returned plaintext once) plus their
    /// SHA-256 hashes for storage.
    (List<string> Plaintext, List<string> Hashes) GenerateRecoveryCodes(int count = 10);

    string HashRecoveryCode(string code);

    // --- pending login challenges (no DB; short-lived in memory) ---
    string CreateChallenge(Guid userId);
    bool TryConsumeChallenge(string challengeId, out Guid userId);
    void Complete(string challengeId);

    // --- per-user 2FA brute-force lockout (audit M3) ---
    /// True if the user has hit the failed-code threshold and is in a cooldown window.
    bool IsLockedOut(Guid userId);
    /// Records one failed 2FA code for the user, arming a lockout once the threshold is reached.
    void RegisterFailedAttempt(Guid userId);
    /// <summary>
    /// Atomically registers a 2FA ATTEMPT and reports whether the user is (now) locked out — the
    /// race-free primitive the 2FA handler should call at the top, BEFORE verifying the code
    /// (audit wave-2 M-3). Counting attempts up front under a single atomic update means concurrent
    /// in-flight guesses are debited immediately, so the check-then-increment window that let a
    /// caller fan out parallel guesses past the threshold no longer exists. Returns true when the
    /// caller must reject. <see cref="ResetFailedAttempts"/> clears the counter on success.
    /// </summary>
    bool TryBeginAttempt(Guid userId);
    /// Clears the failure counter on a successful 2FA.
    void ResetFailedAttempts(Guid userId);
}

public class TotpService : ITotpService
{
    private const string Issuer = "SoftMedia";
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);

    private readonly byte[] _aesKey;
    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime Expires)> _challenges = new();

    // Per-user 2FA failure tracking (audit M3). Keyed by userId so it bounds brute force
    // REGARDLESS of how many challenge ids an attacker mints by re-logging-in. In-memory: a
    // single-instance self-hosted server can't be reset remotely; documented as such.
    private const int MaxFailedAttempts = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<Guid, (int Failures, DateTime? LockedUntil)> _failures = new();

    public TotpService(IConfiguration config)
    {
        // Derive a stable 256-bit AES key from the JWT signing secret. Rotating the JWT
        // secret invalidates stored TOTP secrets (forces re-enrollment) — documented.
        var secret = config.GetSection("JwtSettings")["Secret"]
            ?? throw new InvalidOperationException("JWT Secret is missing");
        _aesKey = SHA256.HashData(Encoding.UTF8.GetBytes("totp-aes::" + secret));
    }

    public TotpEnrollment CreateEnrollment(string username)
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20); // 160-bit, standard for TOTP
        var base32 = Base32Encoding.ToString(secretBytes);
        var label = Uri.EscapeDataString($"{Issuer}:{username}");
        var uri = $"otpauth://totp/{label}?secret={base32}&issuer={Issuer}&digits=6&period=30";
        return new TotpEnrollment(base32, uri);
    }

    public bool VerifyCode(string encryptedSecret, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        string base32;
        try { base32 = DecryptSecret(encryptedSecret); }
        catch { return false; }

        var totp = new Totp(Base32Encoding.ToBytes(base32));
        // VerificationWindow ±1 step (±30s) tolerates clock skew.
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
    }

    public string EncryptSecret(string base32Secret)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(base32Secret);
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        var combined = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private string DecryptSecret(string encrypted)
    {
        var combined = Convert.FromBase64String(encrypted);
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(combined, 16, combined.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }

    public (List<string> Plaintext, List<string> Hashes) GenerateRecoveryCodes(int count = 10)
    {
        var plaintext = new List<string>(count);
        var hashes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            // Audit wave-2 L-10: 80 bits of entropy (10 CSPRNG bytes -> 20 hex chars, dash-grouped
            // for readability). The old 40-bit code was offline-brute-forceable from a DB/backup
            // leak (~10^12 keyspace); 80 bits makes that infeasible regardless of the (fast) hash.
            // Forward-only: existing codes use the same hash function so they still verify — no
            // migration breakage; users get longer codes on their next (re-)enrollment.
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
            var code = string.Join('-', Enumerable.Range(0, 5).Select(g => raw.Substring(g * 4, 4)));
            plaintext.Add(code);
            hashes.Add(HashRecoveryCode(code));
        }
        return (plaintext, hashes);
    }

    public string HashRecoveryCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant()))).ToLowerInvariant();

    public string CreateChallenge(Guid userId)
    {
        PruneExpired();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _challenges[id] = (userId, DateTime.UtcNow.Add(ChallengeTtl));
        return id;
    }

    public bool TryConsumeChallenge(string challengeId, out Guid userId)
    {
        userId = Guid.Empty;
        if (string.IsNullOrEmpty(challengeId)) return false;
        // NOTE: do NOT remove on success here — the caller may need multiple code
        // attempts within the TTL. We only validate existence + expiry; removal happens
        // on successful login (caller calls again is fine) or via TTL prune.
        if (_challenges.TryGetValue(challengeId, out var entry) && entry.Expires > DateTime.UtcNow)
        {
            userId = entry.UserId;
            return true;
        }
        return false;
    }

    /// Removes a challenge once login completes successfully.
    public void Complete(string challengeId) => _challenges.TryRemove(challengeId, out _);

    public bool IsLockedOut(Guid userId)
        => _failures.TryGetValue(userId, out var s) && s.LockedUntil is { } until && until > DateTime.UtcNow;

    public void RegisterFailedAttempt(Guid userId)
    {
        _failures.AddOrUpdate(
            userId,
            _ => (1, null),
            (_, s) =>
            {
                // If a prior lockout already elapsed, start a fresh count.
                if (s.LockedUntil is { } until && until <= DateTime.UtcNow) s = (0, null);
                var failures = s.Failures + 1;
                DateTime? lockedUntil = failures >= MaxFailedAttempts ? DateTime.UtcNow.Add(LockoutDuration) : s.LockedUntil;
                return (failures, lockedUntil);
            });
    }

    public bool TryBeginAttempt(Guid userId)
    {
        // The AddOrUpdate update delegate runs under the dictionary's per-key lock, so concurrent
        // calls for the same user are serialised — each increments exactly once and the call that
        // crosses the threshold arms the lockout that every subsequent in-flight call then sees.
        var state = _failures.AddOrUpdate(
            userId,
            _ => (1, (DateTime?)null),
            (_, s) =>
            {
                // A prior lockout that has elapsed starts a fresh window.
                if (s.LockedUntil is { } until && until <= DateTime.UtcNow) s = (0, null);
                // Already locked and still cooling down — keep as-is (don't extend on each probe).
                if (s.LockedUntil is { } active && active > DateTime.UtcNow) return s;
                var attempts = s.Failures + 1;
                DateTime? lockedUntil = attempts >= MaxFailedAttempts ? DateTime.UtcNow.Add(LockoutDuration) : null;
                return (attempts, lockedUntil);
            });

        return state.LockedUntil is { } lu && lu > DateTime.UtcNow;
    }

    public void ResetFailedAttempts(Guid userId) => _failures.TryRemove(userId, out _);

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _challenges)
            if (kv.Value.Expires <= now) _challenges.TryRemove(kv.Key, out _);
    }
}
