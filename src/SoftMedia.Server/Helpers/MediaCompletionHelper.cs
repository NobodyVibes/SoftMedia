namespace SoftMedia.Server.Helpers;

/// <summary>
/// Single source of truth for "has the user finished (or almost finished) this video?".
/// Shared by the next-episode resolver (<c>RecommendationService.IsEpisodeComplete</c>) and the
/// Continue Watching row so the two never diverge on what counts as "done".
///
/// The rule, in order of precedence:
///   1. An explicit <c>IsWatched</c> flag always wins.
///   2. With a known credits timecode, the item is finished once the user passes it — the most
///      accurate "the story is over" signal (post-credit scenes aside).
///   3. Otherwise, finished once the user passes <see cref="CompletionFraction"/> of the runtime.
/// </summary>
public static class MediaCompletionHelper
{
    /// <summary>Fraction of total runtime past which an item with no credits marker is "finished".</summary>
    public const double CompletionFraction = 0.95;

    public static bool IsComplete(double? playbackPosition, double duration, double? creditsStart, bool isWatched)
    {
        if (isWatched) return true;
        if (duration <= 0) return false;

        var position = playbackPosition ?? 0;

        if (creditsStart.HasValue && creditsStart.Value > 0)
            return position >= creditsStart.Value;

        return position >= duration * CompletionFraction;
    }
}
