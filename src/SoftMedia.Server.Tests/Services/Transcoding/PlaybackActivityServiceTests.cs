using Moq;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// BG-WI-005: "is anyone actually watching" definition — session state x client recency.
public class PlaybackActivityServiceTests
{
    private static PlaybackActivityService Create(params TranscodeSession[] sessions)
    {
        var manager = new Mock<ITranscodeSessionManager>();
        manager.Setup(m => m.GetAllSessions()).Returns(sessions);
        return new PlaybackActivityService(manager.Object);
    }

    private static TranscodeSession Session(TranscodeState state, DateTime lastClientRequest) => new()
    {
        Key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null),
        State = state,
        LastClientRequestTime = lastClientRequest,
    };

    [Fact]
    public void Inactive_WhenNoSessions() =>
        Assert.False(Create().IsPlaybackActive);

    [Theory]
    [InlineData(TranscodeState.Transcoding)]
    [InlineData(TranscodeState.Throttled)]
    public void Active_ForLiveStatesWithRecentClient(TranscodeState state) =>
        Assert.True(Create(Session(state, DateTime.UtcNow)).IsPlaybackActive);

    [Fact]
    public void Inactive_WhenClientWentQuiet_PastTheDormancyWindow() =>
        // Same 90s window ThrottleMonitorService uses before parking the session.
        Assert.False(Create(Session(TranscodeState.Transcoding, DateTime.UtcNow.AddSeconds(-120))).IsPlaybackActive);

    [Theory]
    [InlineData(TranscodeState.Dormant)]
    [InlineData(TranscodeState.Completed)]
    [InlineData(TranscodeState.Failed)]
    public void Inactive_ForParkedStates_EvenWithRecentClient(TranscodeState state) =>
        Assert.False(Create(Session(state, DateTime.UtcNow)).IsPlaybackActive);

    [Fact]
    public void Active_WhenAnyOneSessionIsLive() =>
        Assert.True(Create(
            Session(TranscodeState.Dormant, DateTime.UtcNow),
            Session(TranscodeState.Transcoding, DateTime.UtcNow)).IsPlaybackActive);
}
