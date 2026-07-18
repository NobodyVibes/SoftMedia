using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// R-WI-002 — the per-session stream-plan store. Its contract is what lets a far-seek
/// request (which arrives with only token+sid after the session was DELETEd) still resolve
/// the negotiated quality/security params, so the resolution/codec/HDR decision and the
/// per-user bitrate cap cannot be dropped or bypassed by a client-crafted URL.
public class StreamPlanStoreTests
{
    private static PersistedStreamPlan Plan(int? maxBitrate = 5000) =>
        new(PlaybackMethod.Transcode, "720p", "h264", maxBitrate, PreserveHdr: false);

    [Fact]
    public void Save_Then_Get_RoundTrips()
    {
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();

        store.Save(media, user, "sid-1", Plan(maxBitrate: 5000));
        var got = store.Get(media, user, "sid-1");

        Assert.NotNull(got);
        Assert.Equal(PlaybackMethod.Transcode, got!.Method);
        Assert.Equal("720p", got.Resolution);
        Assert.Equal("h264", got.Codec);
        Assert.Equal(5000, got.MaxBitrate);
        Assert.False(got.PreserveHdr);
    }

    [Fact]
    public void Get_IsIsolatedByMediaUserAndSid()
    {
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();
        store.Save(media, user, "sid-1", Plan());

        Assert.Null(store.Get(Guid.NewGuid(), user, "sid-1")); // different media
        Assert.Null(store.Get(media, Guid.NewGuid(), "sid-1")); // different user (no cross-user leak)
        Assert.Null(store.Get(media, user, "sid-2"));           // different session
    }

    [Fact]
    public void SidLess_IsStateless()
    {
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();

        // DirectPlay / sid-less transcode requests must not persist anything…
        store.Save(media, user, null, Plan());
        store.Save(media, user, "", Plan());

        // …and a sid-less lookup always returns null (falls back to today's URL-param behaviour).
        Assert.Null(store.Get(media, user, null));
        Assert.Null(store.Get(media, user, ""));
    }

    [Fact]
    public void Save_OverwritesSameSession()
    {
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();

        store.Save(media, user, "sid-1", Plan(maxBitrate: 8000));
        store.Save(media, user, "sid-1", Plan(maxBitrate: 3000)); // re-plan (e.g. track switch)

        Assert.Equal(3000, store.Get(media, user, "sid-1")!.MaxBitrate);
    }

    [Fact]
    public void Save_RejectsMalformedSid()
    {
        // diff-review MEDIUM: an over-long / illegal-charset sid must not be persisted (it can
        // never be resolved — GetMasterPlaylist validates the same way — and would inflate the
        // dictionary key). TranscodeSid caps at 64 chars, charset [A-Za-z0-9_-].
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();

        var tooLong = new string('a', 100);
        var illegal = "bad/../sid";
        store.Save(media, user, tooLong, Plan());
        store.Save(media, user, illegal, Plan());

        Assert.Null(store.Get(media, user, tooLong));
        Assert.Null(store.Get(media, user, illegal));
    }

    [Fact]
    public void Save_EnforcesHardSizeCap()
    {
        // diff-review MEDIUM: cycling unique sids must not grow the store without bound. Save well
        // past the 2048 hard cap, then count how many still resolve — the store must be bounded at
        // the cap (which specific entries survive depends on eviction order and is not asserted).
        var store = new StreamPlanStore();
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();

        for (int i = 0; i < 3000; i++)
            store.Save(media, user, "sid" + i, Plan(maxBitrate: i));

        var present = Enumerable.Range(0, 3000).Count(i => store.Get(media, user, "sid" + i) != null);
        Assert.True(present <= 2048, $"store not bounded: {present} entries present");
        Assert.True(present > 0, "eviction nuked the whole store");
    }
}
