using System.Collections.Concurrent;

namespace SoftMedia.Server.Services.Sessions;

/// <summary>
/// Which client is playing, for the admin Now-Playing dashboard: a coarse form factor
/// (see <see cref="Infrastructure.ClientDeviceClassifier"/>) plus the client address.
/// Captured per request and refreshed as the session continues, so a resumed play shows
/// the device that is playing NOW rather than whichever one started it.
/// </summary>
public sealed record ClientDevice(string DeviceType, string? IpAddress);

/// <summary>
/// R-WI-016 — tracks DIRECT-PLAY streams (video direct play + all music) for the
/// admin Now-Playing dashboard. Transcodes are already tracked by
/// <see cref="Transcoding.TranscodeSessionManager"/>; this registry covers the
/// other half, where playback is a plain <c>PhysicalFile</c> range response with
/// no server-side session object.
///
/// Tracking model (per the plan's design note): a per-request "touch" cannot work —
/// range processing typically means ONE long-lived request per play (or none at all
/// once the client has buffered the whole file) — so liveness combines
/// (a) response lifetime: registered when a /stream response starts, released when
///     it completes (works for the single 2-hour-request case), and
/// (b) the ~10s interaction progress beats as a heartbeat (works for the
///     fully-buffered case and carries the playhead position),
/// with an idle-expiry window pruning entries that have neither.
/// </summary>
public interface IActiveStreamRegistry
{
    /// <summary>
    /// A /stream response began for this user+media (GET only, not HEAD probes).
    /// Returns the entry as a HANDLE: the caller must pass the same instance to
    /// <see cref="OnResponseEnded"/>. Key-based release could decrement a DIFFERENT
    /// generation of the entry after a prune/evict recreated it (review MED), silently
    /// unbalancing a live play's refcount.
    /// </summary>
    DirectPlayEntry OnResponseStarted(Guid userId, Guid mediaId, ClientDevice? device = null);

    /// <summary>The /stream response completed (including client aborts).</summary>
    void OnResponseEnded(DirectPlayEntry entry);

    /// <summary>
    /// Progress-beat heartbeat: refreshes liveness/playhead, creating the entry if
    /// missing. Creation matters for plays with no live /stream response on THIS
    /// process — fully browser-cached files (no request ever) and plays surviving a
    /// server restart (registry is in-memory; found live: the playing track vanished
    /// from the dashboard after a restart). The CALLER must not invoke this while the
    /// user has an active transcode session for the media — beats fire during
    /// transcodes too, and would double-list them as phantom direct plays (that guard
    /// lives in InteractionController, where the transcode registry is visible).
    /// </summary>
    void TouchOrCreate(Guid userId, Guid mediaId, double positionSeconds, ClientDevice? device = null);

    /// <summary>Live entries; expired ones are pruned as a side effect.</summary>
    IReadOnlyList<DirectPlayEntry> GetActiveEntries();
}

public sealed class DirectPlayEntry
{
    private readonly Func<DateTime> _clock;

    internal DirectPlayEntry(Guid userId, Guid mediaId, Func<DateTime> clock)
    {
        _clock = clock;
        UserId = userId;
        MediaId = mediaId;
        StartedAt = clock();
        _lastSeenTicks = StartedAt.Ticks;
    }

    public Guid UserId { get; }
    public Guid MediaId { get; }
    public DateTime StartedAt { get; }

    // Mutable liveness state — writes are cheap stamps from the hot streaming path.
    private long _lastSeenTicks;
    private int _activeResponses;
    private long _positionBits;

    public DateTime LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);
    public int ActiveResponses => Volatile.Read(ref _activeResponses);
    public double PositionSeconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _positionBits));

    /// <summary>
    /// True once a progress beat has arrived. Distinguishes actual PLAYBACK from a
    /// mere open stream — the music player's gapless PRELOAD fetches the next track
    /// through /stream without playing it (found live: the preloaded track showed as
    /// a second "Playing" row at 0:00).
    /// </summary>
    public bool HasHeartbeat => Volatile.Read(ref _hasHeartbeat) == 1;
    private int _hasHeartbeat;

    /// <summary>
    /// The most recent client seen on this entry. Reference-swapped (never mutated in
    /// place) so dashboard reads on another thread always observe a consistent pair
    /// rather than a half-updated device/IP.
    /// </summary>
    public ClientDevice? Device => Volatile.Read(ref _device);
    private ClientDevice? _device;

    internal void SetDevice(ClientDevice? device)
    {
        if (device is not null) Volatile.Write(ref _device, device);
    }

    internal void ResponseStarted()
    {
        Interlocked.Increment(ref _activeResponses);
        Stamp();
    }

    internal void ResponseEnded()
    {
        // Clamp at 0: a registry entry can be evicted+recreated between a response's
        // start and end, so an end can arrive for a fresh entry.
        int current;
        do
        {
            current = Volatile.Read(ref _activeResponses);
            if (current <= 0) break;
        } while (Interlocked.CompareExchange(ref _activeResponses, current - 1, current) != current);
        Stamp();
    }

    internal void Touch(double positionSeconds)
    {
        Interlocked.Exchange(ref _positionBits, BitConverter.DoubleToInt64Bits(positionSeconds));
        Interlocked.Exchange(ref _hasHeartbeat, 1);
        Stamp();
    }

    private void Stamp() => Interlocked.Exchange(ref _lastSeenTicks, _clock().Ticks);
}

public sealed class ActiveStreamRegistry : IActiveStreamRegistry
{
    /// <summary>
    /// No beat and no open response for this long → the play is over. Beats arrive
    /// every ~10s while playing; 60s tolerates pauses briefly outlasting a beat gap
    /// without keeping dead rows around for long.
    /// </summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(60);

    /// <summary>DoS bound: an authenticated user hammering /stream with distinct ids
    /// must not grow the registry without limit. Far above any home-server reality.</summary>
    private const int HardEntryCap = 512;

    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<(Guid UserId, Guid MediaId), DirectPlayEntry> _entries = new();

    public ActiveStreamRegistry() : this(() => DateTime.UtcNow) { }

    /// <summary>Test seam — production uses the UTC wall clock.</summary>
    public ActiveStreamRegistry(Func<DateTime> clock) => _clock = clock;

    public DirectPlayEntry OnResponseStarted(Guid userId, Guid mediaId, ClientDevice? device = null)
    {
        var added = false;
        var entry = _entries.GetOrAdd((userId, mediaId), key =>
        {
            added = true;
            return new DirectPlayEntry(key.UserId, key.MediaId, _clock);
        });
        entry.SetDevice(device);
        entry.ResponseStarted();

        // ConcurrentDictionary.Count takes every bucket lock — only pay it when this
        // call may actually have grown the dictionary.
        if (added && _entries.Count > HardEntryCap) EvictStalest();
        return entry;
    }

    public void OnResponseEnded(DirectPlayEntry entry) => entry.ResponseEnded();

    public void TouchOrCreate(Guid userId, Guid mediaId, double positionSeconds, ClientDevice? device = null)
    {
        var added = false;
        var entry = _entries.GetOrAdd((userId, mediaId), key =>
        {
            added = true;
            return new DirectPlayEntry(key.UserId, key.MediaId, _clock);
        });
        entry.SetDevice(device);
        entry.Touch(positionSeconds);

        if (added && _entries.Count > HardEntryCap) EvictStalest();
    }

    public IReadOnlyList<DirectPlayEntry> GetActiveEntries()
    {
        var cutoff = _clock() - IdleWindow;
        var live = new List<DirectPlayEntry>();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ActiveResponses <= 0 && entry.LastSeenAt < cutoff)
            {
                _entries.TryRemove(key, out _);
                // Re-check after removal: a response/beat can revive the entry between
                // the staleness check and the remove. Re-adding the SAME instance keeps
                // its refcount; if another generation already took the slot, the handle
                // API keeps that response's release safe anyway.
                if ((entry.ActiveResponses > 0 || entry.LastSeenAt >= cutoff) && _entries.TryAdd(key, entry))
                {
                    live.Add(entry);
                }
            }
            else
            {
                live.Add(entry);
            }
        }
        return live;
    }

    /// <summary>
    /// Cap-breach path only. The cap is soft against entries with OPEN responses (they
    /// are never evicted — a flood of concurrent /stream connections is bounded by
    /// Kestrel's connection limits, not here). Entries WITHOUT a heartbeat (preloads,
    /// abandoned fetches) go first so a flood cannot evict real beat-tracked plays.
    /// </summary>
    private void EvictStalest()
    {
        foreach (var (key, entry) in _entries
                     .OrderBy(kv => kv.Value.HasHeartbeat)
                     .ThenBy(kv => kv.Value.LastSeenAt))
        {
            if (_entries.Count <= HardEntryCap) break;
            if (entry.ActiveResponses <= 0) _entries.TryRemove(key, out _);
        }
    }
}
