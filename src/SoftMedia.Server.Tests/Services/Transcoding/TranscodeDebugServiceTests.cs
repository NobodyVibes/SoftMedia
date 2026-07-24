using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// <summary>
/// SR-WI-024: the debug endpoint must resolve sid-keyed sessions (the key was built without
/// StreamId, so every sid-keyed lookup missed and reported "likely direct play"), and its
/// <c>toneMapped</c> flag must reflect the ACTUAL pipeline (SR-WI-023) — not the old
/// <c>IsSourceHdr &amp;&amp; !PreserveHdr</c> guess that lied for remux and h264-preserve.
/// </summary>
public class TranscodeDebugServiceTests
{
    private readonly Guid _mediaId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Mock<ITranscodeSessionManager> _sessionManager = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IStreamPlanService> _streamPlan = new();
    private readonly Mock<IBinaryLocationService> _binaries = new();

    private TranscodeDebugService BuildService()
    {
        // Every setting read falls back to its supplied default.
        _settings.Setup(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string def) => def);

        _streamPlan.Setup(p => p.ComputeStreamPlanAsync(
                It.IsAny<Guid>(), It.IsAny<MediaItem>(), It.IsAny<ClientCapabilities>(), It.IsAny<string>(),
                It.IsAny<System.Net.IPAddress?>(), It.IsAny<int?>()))
            .ReturnsAsync(new StreamPlan { Method = PlaybackMethod.Transcode, VideoCodec = "h264" });

        var repo = new Mock<IMediaRepository>();
        repo.Setup(r => r.GetByIdAsync(_mediaId))
            .ReturnsAsync(new MediaItem { Id = _mediaId, Title = "Movie", Path = @"C:\media\movie.mkv" });

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IMediaRepository))).Returns(repo.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new TranscodeDebugService(
            _sessionManager.Object,
            _settings.Object,
            _streamPlan.Object,
            _binaries.Object,
            scopeFactory.Object,
            NullLogger<TranscodeDebugService>.Instance);
    }

    private TranscodeSession MakeSession(string? sid, bool isSourceHdr = false, bool preserveHdr = false,
        bool isRemux = false, string? targetCodec = "h264", bool burnSubtitles = false)
    {
        var key = new TranscodeSessionKey(_mediaId, _userId, null, sid);
        var session = new TranscodeSession
        {
            Key = key,
            UserId = _userId,
            // Nonexistent directory: the output probe cleanly reports "no files" without ffprobe.
            SessionDirectory = Path.Combine(Path.GetTempPath(), "softmedia-tests", Guid.NewGuid().ToString("N")),
            IsSourceHdr = isSourceHdr,
            PreserveHdr = preserveHdr,
            IsRemux = isRemux,
            TargetCodec = targetCodec,
            BurnSubtitles = burnSubtitles,
        };
        _sessionManager.Setup(m => m.GetSession(key)).Returns(session);
        return session;
    }

    private static object? Prop(object obj, string name) =>
        obj.GetType().GetProperty(name)?.GetValue(obj);

    private static object? Nested(object obj, string outer, string inner)
    {
        var o = Prop(obj, outer);
        Assert.NotNull(o);
        return Prop(o!, inner);
    }

    // ---- SR-WI-024: sid threading ----

    [Fact]
    public async Task Sid_keyed_session_is_resolved_when_sid_is_passed()
    {
        MakeSession(sid: "abc123");
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, sub: null, isAdmin: true, sid: "abc123");

        Assert.Equal("Transcode", Prop(result, "playbackMode"));
        Assert.Equal(true, Prop(result, "isTranscoding"));
    }

    [Fact]
    public async Task Sid_keyed_session_is_missed_without_the_sid_the_pre_fix_behaviour()
    {
        // Guards the regression direction: a session keyed with a sid must NOT be found by a
        // sid-less lookup (distinct sessions), which is why threading sid through matters.
        MakeSession(sid: "abc123");
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, sub: null, isAdmin: true, sid: null);

        Assert.Equal("DirectPlay", Prop(result, "playbackMode"));
    }

    [Fact]
    public async Task Sidless_session_still_resolves_without_sid()
    {
        MakeSession(sid: null);
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, sub: null, isAdmin: true, sid: null);

        Assert.Equal("Transcode", Prop(result, "playbackMode"));
    }

    // ---- SR-WI-023: toneMapped reflects the actual pipeline ----

    [Fact]
    public async Task ToneMapped_true_for_hdr_transcode_without_preserve()
    {
        MakeSession(sid: "s1", isSourceHdr: true, preserveHdr: false, targetCodec: "h264");
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(true, Nested(result, "decision", "toneMapped"));
    }

    [Fact]
    public async Task ToneMapped_false_for_remux_even_when_source_is_hdr()
    {
        // Remux stream-copies — nothing is tone mapped, whatever the old formula claimed.
        MakeSession(sid: "s1", isSourceHdr: true, preserveHdr: false, isRemux: true);
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(false, Nested(result, "decision", "toneMapped"));
    }

    [Fact]
    public async Task ToneMapped_false_when_hdr_is_preserved_into_hevc()
    {
        MakeSession(sid: "s1", isSourceHdr: true, preserveHdr: true, targetCodec: "hevc");
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(false, Nested(result, "decision", "toneMapped"));
        Assert.Equal(true, Nested(result, "decision", "preserveHdr"));
    }

    [Fact]
    public async Task ToneMapped_true_when_preserve_is_requested_but_output_is_h264()
    {
        // The builder overrides PreserveHDR for 8-bit h264 output (SR-WI-023 #5) — the debug
        // panel must report what actually happens.
        MakeSession(sid: "s1", isSourceHdr: true, preserveHdr: true, targetCodec: "h264");
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(true, Nested(result, "decision", "toneMapped"));
        Assert.Equal(false, Nested(result, "decision", "preserveHdr"));
    }

    [Fact]
    public async Task ToneMapped_true_when_preserved_hdr_has_burned_subtitles()
    {
        // Subtitle burn-in forces tone mapping even under PreserveHDR (both pipelines).
        MakeSession(sid: "s1", isSourceHdr: true, preserveHdr: true, targetCodec: "hevc", burnSubtitles: true);
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(true, Nested(result, "decision", "toneMapped"));
    }

    [Fact]
    public async Task ToneMapped_false_for_sdr_source()
    {
        MakeSession(sid: "s1", isSourceHdr: false);
        var service = BuildService();

        var result = await service.GetDebugInfoAsync(_mediaId, _userId, null, null, true, "s1");

        Assert.Equal(false, Nested(result, "decision", "toneMapped"));
    }
}
