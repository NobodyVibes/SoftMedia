using System.Collections.Concurrent;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// AA-WI-011 — short-TTL cache for the per-request media/cast-token user-eligibility
/// recheck (audit wave-2 L-3). The recheck is the revocation point for otherwise
/// stateless tokens, but it cost one scoped DbContext + Users query PER REQUEST — one
/// hit per HLS segment, and once artwork is token-gated (AA-WI-001) one hit per poster
/// on screen. This cache collapses that to ~one query per user per TTL window while
/// keeping revocation effectively instant: every admin ban / un-approve / delete write
/// path calls <see cref="Invalidate"/>, so the TTL only bounds staleness for direct DB
/// edits made outside the app.
/// </summary>
public interface IUserEligibilityCache
{
    /// <summary>True if a non-expired verdict exists for the user.</summary>
    bool TryGet(Guid userId, out bool eligible);

    /// <summary>Record a fresh verdict for the user (expires after the TTL).</summary>
    void Set(Guid userId, bool eligible);

    /// <summary>
    /// Drop the user's cached verdict. Call from EVERY write path that changes
    /// IsBanned / IsDeleted / IsApproved so the next token use re-reads the DB.
    /// </summary>
    void Invalidate(Guid userId);
}

public class UserEligibilityCache : IUserEligibilityCache
{
    /// <summary>
    /// Upper bound on staleness for eligibility changes that bypass the app's write
    /// paths (direct DB edits). App-driven changes are eager-invalidated and take
    /// effect on the next request regardless of this value. Settable so tests can
    /// exercise expiry without waiting (project convention; no InternalsVisibleTo).
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<Guid, (bool Eligible, DateTime ExpiresUtc)> _entries = new();

    public bool TryGet(Guid userId, out bool eligible)
    {
        if (_entries.TryGetValue(userId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            eligible = entry.Eligible;
            return true;
        }
        eligible = false;
        return false;
    }

    public void Set(Guid userId, bool eligible)
        => _entries[userId] = (eligible, DateTime.UtcNow + Ttl);

    public void Invalidate(Guid userId)
        => _entries.TryRemove(userId, out _);
}
