using SoftMedia.Server.Services.Sessions;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Sessions;

/// R-WI-016 — direct-play liveness registry. Pins the load-bearing rules:
/// (1) beats create-or-refresh (the transcode phantom-guard lives in
///     InteractionController, which must not call TouchOrCreate during a LIVE
///     transcode), and a heartbeat marks real playback vs a mere open stream,
/// (2) liveness = open response OR recent heartbeat; both gone past the idle
///     window → pruned,
/// (3) an open long-lived response keeps the entry alive with no beats at all
///     (the one-request-per-movie range-processing case),
/// (4) response release is HANDLE-based so a prune/evict recreating the entry can
///     never unbalance a different generation's refcount.
public class ActiveStreamRegistryTests
{
    private DateTime _now = new(2026, 07, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly ActiveStreamRegistry _registry;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _media = Guid.NewGuid();

    public ActiveStreamRegistryTests() => _registry = new ActiveStreamRegistry(() => _now);

    [Fact]
    public void ResponseStart_CreatesALiveEntry()
    {
        _registry.OnResponseStarted(_user, _media);

        var entry = Assert.Single(_registry.GetActiveEntries());
        Assert.Equal(_user, entry.UserId);
        Assert.Equal(_media, entry.MediaId);
        Assert.Equal(1, entry.ActiveResponses);
    }

    [Fact]
    public void TouchOrCreate_CreatesAHeartbeatEntry_WithThePlayhead()
    {
        // Beats can create: fully browser-cached plays never hit /stream, and a
        // server restart wipes the in-memory registry mid-play. (The no-phantom
        // guard for transcode viewers lives in InteractionController.)
        _registry.TouchOrCreate(_user, _media, 42);

        var entry = Assert.Single(_registry.GetActiveEntries());
        Assert.Equal(42, entry.PositionSeconds);
        Assert.True(entry.HasHeartbeat);
        Assert.Equal(0, entry.ActiveResponses);
    }

    [Fact]
    public void HasHeartbeat_DistinguishesPlaybackFromAnOpenStream()
    {
        // The music player's gapless PRELOAD opens a /stream response without
        // playing — only a progress beat proves actual playback.
        _registry.OnResponseStarted(_user, _media);
        Assert.False(Assert.Single(_registry.GetActiveEntries()).HasHeartbeat);

        _registry.TouchOrCreate(_user, _media, 5);
        Assert.True(Assert.Single(_registry.GetActiveEntries()).HasHeartbeat);
    }

    [Fact]
    public void OpenResponse_SurvivesTheIdleWindow_WithoutBeats()
    {
        _registry.OnResponseStarted(_user, _media);

        _now += ActiveStreamRegistry.IdleWindow + TimeSpan.FromMinutes(90); // 2h movie, single request
        Assert.Single(_registry.GetActiveEntries());
    }

    [Fact]
    public void ClosedResponse_ExpiresAfterIdleWindow_AndBeatsPostponeIt()
    {
        var handle = _registry.OnResponseStarted(_user, _media);
        _registry.OnResponseEnded(handle);

        // Fully buffered: response closed, beats keep it alive.
        _now += TimeSpan.FromSeconds(45);
        _registry.TouchOrCreate(_user, _media, 45);
        _now += TimeSpan.FromSeconds(45);
        Assert.Single(_registry.GetActiveEntries()); // last beat 45s ago < 60s window

        _now += ActiveStreamRegistry.IdleWindow;     // no beat since → the play ended
        Assert.Empty(_registry.GetActiveEntries());

        // A later beat re-creates it — beats MEAN playback (resume after a long pause).
        _registry.TouchOrCreate(_user, _media, 99);
        Assert.Equal(99, Assert.Single(_registry.GetActiveEntries()).PositionSeconds);
    }

    [Fact]
    public void OverlappingResponses_AreRefCounted()
    {
        var first = _registry.OnResponseStarted(_user, _media);
        var second = _registry.OnResponseStarted(_user, _media); // second range request for the same play

        _registry.OnResponseEnded(first);
        _now += ActiveStreamRegistry.IdleWindow + TimeSpan.FromSeconds(1);
        Assert.Single(_registry.GetActiveEntries()); // one response still open

        _registry.OnResponseEnded(second);
        _now += ActiveStreamRegistry.IdleWindow + TimeSpan.FromSeconds(1);
        Assert.Empty(_registry.GetActiveEntries());
    }

    [Fact]
    public void UnbalancedEnd_ClampsAtZero_InsteadOfGoingNegative()
    {
        var h = _registry.OnResponseStarted(_user, _media);
        _registry.OnResponseEnded(h);
        _registry.OnResponseEnded(h); // stray double-completion

        var entry = Assert.Single(_registry.GetActiveEntries());
        Assert.Equal(0, entry.ActiveResponses);

        // A fresh play must still work (a negative count would never release).
        _registry.OnResponseStarted(_user, _media);
        Assert.Equal(1, Assert.Single(_registry.GetActiveEntries()).ActiveResponses);
    }

    [Fact]
    public void DistinctUsersAndMedia_AreSeparateEntries()
    {
        var user2 = Guid.NewGuid();
        _registry.OnResponseStarted(_user, _media);
        _registry.OnResponseStarted(user2, _media);

        Assert.Equal(2, _registry.GetActiveEntries().Count);
    }

    [Fact]
    public void HandleRelease_AfterPruneAndRecreation_CannotUnbalanceTheNewGeneration()
    {
        // Review MED: key-based release could decrement a RECREATED entry's count.
        // With handles, releasing a pruned generation must leave the new one intact.
        var oldHandle = _registry.OnResponseStarted(_user, _media);
        _registry.OnResponseEnded(oldHandle);
        _now += ActiveStreamRegistry.IdleWindow + TimeSpan.FromSeconds(1);
        Assert.Empty(_registry.GetActiveEntries()); // old generation pruned

        var fresh = _registry.OnResponseStarted(_user, _media); // new generation, count 1
        _registry.OnResponseEnded(oldHandle); // stale completion from the dead response

        Assert.Equal(1, fresh.ActiveResponses); // untouched — the play stays tracked
        _now += ActiveStreamRegistry.IdleWindow + TimeSpan.FromSeconds(1);
        Assert.Single(_registry.GetActiveEntries());
    }
}
