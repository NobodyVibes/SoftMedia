using System.Collections.Concurrent;
using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Services.Transcoding;

/// <summary>
/// The authoritative quality/security parameters the server negotiated for a transcode
/// session, persisted independently of the <c>TranscodeSession</c> lifecycle (R-WI-002).
/// </summary>
public sealed record PersistedStreamPlan(
    PlaybackMethod Method,
    string? Resolution,
    string? Codec,
    int? MaxBitrate,
    bool PreserveHdr,
    bool AudioCopy = false,
    string? AudioCodec = null,
    int AudioChannels = 0,
    // QS-WI-003: the winning clamp reason code from plan negotiation (e.g. "bitrate.wan-cap"),
    // so the admin Now Playing card can show WHY a session is capped. Null = nothing clamped.
    string? LimitReasonCode = null);

/// <summary>
/// Per-session store of the negotiated stream plan, keyed by (mediaId, userId, sid).
///
/// Its whole reason to exist is a lifetime <b>independent of <c>TranscodeSession</c></b>:
/// the client's far-seek flow issues <c>DELETE /api/transcode/{id}?sid=…</c> (destroying the
/// session) and then re-requests <c>master.m3u8</c> with only <c>token+sid</c>. If the quality
/// params lived on the session, they would be gone at exactly that moment; here they survive,
/// so the controller can restore the negotiated resolution/codec/HDR and re-apply the per-user
/// bitrate cap instead of trusting the (now minimal, client-controlled) query string.
/// </summary>
public interface IStreamPlanStore
{
    /// Persist the plan for a session. No-op when <paramref name="sid"/> is empty
    /// (DirectPlay and sid-less requests are stateless by design).
    void Save(Guid mediaId, Guid userId, string? sid, PersistedStreamPlan plan);

    /// The persisted plan for a session, or null if none / expired / sid-less.
    PersistedStreamPlan? Get(Guid mediaId, Guid userId, string? sid);
}

public sealed class StreamPlanStore : IStreamPlanStore
{
    // A plan only needs to outlive a viewing session. 12h comfortably covers the longest
    // movie plus pauses. Growth is bounded two ways: the sid is validated before persistence
    // (rejecting oversized/illegal keys), and the map is capped at a HARD size that evicts the
    // soonest-to-expire entries — so an authenticated client cycling unique sids cannot grow it
    // without bound (diff-review MEDIUM).
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
    private const int MaxEntries = 2048;

    private readonly ConcurrentDictionary<string, Entry> _plans = new();

    private readonly record struct Entry(PersistedStreamPlan Plan, DateTime ExpiresAtUtc);

    private static string Key(Guid mediaId, Guid userId, string sid) => $"{mediaId:N}|{userId:N}|{sid}";

    public void Save(Guid mediaId, Guid userId, string? sid, PersistedStreamPlan plan)
    {
        // Sid-less requests are stateless by design; and only persist for a well-formed sid.
        // TranscodeSid.IsValid bounds length/charset (note it returns true for empty, so the
        // explicit emptiness check is required), so the dictionary key can't be attacker-inflated,
        // and an invalid sid could never be resolved by GetMasterPlaylist (which validates the
        // same way) — persisting it would be dead weight.
        if (string.IsNullOrEmpty(sid) || !TranscodeSid.IsValid(sid)) return;

        _plans[Key(mediaId, userId, sid)] = new Entry(plan, DateTime.UtcNow + Ttl);

        if (_plans.Count > MaxEntries) Prune();
    }

    public PersistedStreamPlan? Get(Guid mediaId, Guid userId, string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return null;
        var key = Key(mediaId, userId, sid);
        if (_plans.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAtUtc)
            {
                // Sliding expiration: an actively-playing session reloads master.m3u8 periodically,
                // so refresh the TTL on each resolve. This keeps a live plan from expiring
                // mid-playback — which would otherwise flip a running remux session into a full
                // re-encode (R-WI-003 review). The plan lapses 12h after the last request.
                _plans[key] = entry with { ExpiresAtUtc = DateTime.UtcNow + Ttl };
                return entry.Plan;
            }
            _plans.TryRemove(key, out _); // expired
        }
        return null;
    }

    /// Drop expired entries first; if still over the hard cap, evict the soonest-to-expire ones
    /// until under it. Bounds the map size regardless of TTL and request rate.
    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _plans)
        {
            if (kv.Value.ExpiresAtUtc <= now)
                _plans.TryRemove(kv.Key, out _);
        }

        var over = _plans.Count - MaxEntries;
        if (over <= 0) return;

        foreach (var kv in _plans.OrderBy(kv => kv.Value.ExpiresAtUtc).Take(over))
        {
            _plans.TryRemove(kv.Key, out _);
        }
    }
}
