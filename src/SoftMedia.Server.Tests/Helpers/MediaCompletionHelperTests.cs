using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// The single "is this video finished?" rule shared by the next-episode resolver and the
/// Continue Watching row. Guards the precedence: explicit watched flag &gt; credits timecode &gt; 95%.
/// </summary>
public class MediaCompletionHelperTests
{
    [Fact]
    public void IsWatched_flag_always_wins_even_at_zero_position()
    {
        Assert.True(MediaCompletionHelper.IsComplete(playbackPosition: 0, duration: 3600, creditsStart: null, isWatched: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositive_duration_is_never_complete(double duration)
    {
        // Can't reason about progress without a runtime — must not be treated as finished.
        Assert.False(MediaCompletionHelper.IsComplete(playbackPosition: 999999, duration: duration, creditsStart: null, isWatched: false));
    }

    [Theory]
    [InlineData(3420, true)]   // 95% of 3600 — exactly the threshold counts as complete
    [InlineData(3599, true)]   // ~100%
    [InlineData(3000, false)]  // ~83% — still in progress
    [InlineData(0, false)]     // not started
    public void Without_credits_uses_95_percent_threshold(double position, bool expected)
    {
        Assert.Equal(expected, MediaCompletionHelper.IsComplete(position, duration: 3600, creditsStart: null, isWatched: false));
    }

    [Fact]
    public void Credits_timecode_completes_before_95_percent()
    {
        // Credits start at 50% — passing them finishes the story even though < 95% of runtime.
        Assert.True(MediaCompletionHelper.IsComplete(playbackPosition: 1900, duration: 3600, creditsStart: 1800, isWatched: false));
    }

    [Fact]
    public void Credits_timecode_overrides_the_95_percent_rule()
    {
        // Late credits (99%): a viewer at 96% has passed 95% but NOT the credits, so the climax
        // isn't over yet — credits win over the fraction.
        Assert.False(MediaCompletionHelper.IsComplete(playbackPosition: 3456, duration: 3600, creditsStart: 3564, isWatched: false));
    }

    [Fact]
    public void Null_position_is_not_complete()
    {
        Assert.False(MediaCompletionHelper.IsComplete(playbackPosition: null, duration: 3600, creditsStart: null, isWatched: false));
    }
}
