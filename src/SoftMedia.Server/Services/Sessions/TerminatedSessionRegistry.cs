using System.Collections.Concurrent;

namespace SoftMedia.Server.Services.Sessions;

/// <summary>
/// Remembers transcode sessions an admin has STOPPED, so they stay stopped.
///
/// Killing the session alone is not enough: the player notices its segments failing,
/// hls.js "recovers" by reloading the playlist, and <c>master.m3u8</c> cheerfully starts
/// a brand-new transcode under the SAME sid — so ffmpeg respawns and playback carries on,
/// making the admin's Stop look like it did nothing (reproduced live: session gone and
/// ffmpeg killed, then both back within seconds of the client's next request).
///
/// A terminated session is therefore tombstoned for a short window and further requests
/// for it are refused. Keyed INCLUDING the sid, which is minted per playback instance:
/// the client's automatic recovery reuses the same sid and stays blocked, while a
/// deliberate new play mints a new sid and is unaffected — so an admin's Stop is not a
/// lockout. The window only needs to outlast the client's retry loop (seconds).
/// </summary>
public interface ITerminatedSessionRegistry
{
    void MarkTerminated(Guid mediaId, Guid userId, string? sid);
    bool IsTerminated(Guid mediaId, Guid userId, string? sid);

    /// <summary>
    /// True when ANY session for this user+media was stopped in the last few seconds,
    /// regardless of sid. Used to stop a beat from CREATING a direct-play row: a stopped
    /// player keeps beating while it drains its buffer and retries, and the beat handler's
    /// "is a transcode live?" guard no longer fires (the admin just removed that session) —
    /// so the beats registered the movie as a DIRECT PLAY and the dashboard showed the same
    /// title twice once the viewer pressed play again.
    ///
    /// Deliberately NOT <see cref="IsTerminated"/>: that one is sid-keyed, which is what
    /// makes its 2-minute window safe (a deliberate new play mints a new sid and escapes at
    /// once). Dropping the sid removes that escape hatch, so this uses its own much shorter
    /// window — see <see cref="TerminatedSessionRegistry.BeatCreationWindow"/>.
    /// </summary>
    bool WasRecentlyTerminatedForUser(Guid mediaId, Guid userId);
}

public sealed class TerminatedSessionRegistry : ITerminatedSessionRegistry
{
    /// Long enough to outlast any client retry loop, short enough that a sid-less client
    /// (the SPA always sends one) is never locked out for meaningfully long.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a stopped player's beats are barred from CREATING a direct-play row. Sized
    /// to the client's reaction time, not to <see cref="Ttl"/>: the player halts on the 410
    /// rather than retrying indefinitely, so this only has to outlast the buffer it was
    /// already draining. Kept short because it is sid-agnostic — every play of that title
    /// waits it out, so the cost of overshooting is a genuinely-playing title missing from
    /// the dashboard.
    /// </summary>
    public static readonly TimeSpan BeatCreationWindow = TimeSpan.FromSeconds(30);

    /// Bounds memory if something ever terminates in a loop; oldest entries go first.
    private const int HardCap = 512;

    private readonly ConcurrentDictionary<(Guid MediaId, Guid UserId, string? Sid), DateTime> _tombstones = new();
    private readonly Func<DateTime> _clock;

    public TerminatedSessionRegistry() : this(() => DateTime.UtcNow) { }

    /// Test seam for the expiry window.
    internal TerminatedSessionRegistry(Func<DateTime> clock) => _clock = clock;

    public void MarkTerminated(Guid mediaId, Guid userId, string? sid)
    {
        _tombstones[(mediaId, userId, sid)] = _clock();
        if (_tombstones.Count > HardCap) Prune(force: true);
    }

    public bool IsTerminated(Guid mediaId, Guid userId, string? sid)
    {
        if (!_tombstones.TryGetValue((mediaId, userId, sid), out var at)) return false;
        if (_clock() - at <= Ttl) return true;

        // Expired — drop it so a later play of the same item isn't re-checked forever.
        _tombstones.TryRemove((mediaId, userId, sid), out _);
        return false;
    }

    public bool WasRecentlyTerminatedForUser(Guid mediaId, Guid userId)
    {
        var now = _clock();
        foreach (var (key, at) in _tombstones)
        {
            if (key.MediaId == mediaId && key.UserId == userId && now - at <= BeatCreationWindow) return true;
        }
        return false;
    }

    private void Prune(bool force)
    {
        var now = _clock();
        foreach (var (key, at) in _tombstones)
        {
            if (now - at > Ttl) _tombstones.TryRemove(key, out _);
        }
        if (!force || _tombstones.Count <= HardCap) return;

        // Still over cap after expiry pruning: evict the oldest.
        foreach (var (key, _) in _tombstones.OrderBy(kv => kv.Value).Take(_tombstones.Count - HardCap))
        {
            _tombstones.TryRemove(key, out _);
        }
    }
}
