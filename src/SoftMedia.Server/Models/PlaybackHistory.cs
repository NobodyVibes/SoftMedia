namespace SoftMedia.Server.Models;

/// <summary>
/// R-WI-013 — one row per PLAY of a video/audio item by a user (distinct from
/// <see cref="UserMediaInteraction"/>, which holds only the CURRENT state — resume position,
/// watched flag). Rows are opened by the progress-beat flow once playback crosses the play
/// threshold, updated by subsequent beats, and closed by the completion rule
/// (<see cref="Helpers.MediaCompletionHelper"/>). Powers watch/listen history, "most played",
/// and future recommendations. Local-only like everything else; the read API is self-scoped —
/// users only ever see their own rows.
/// </summary>
public class PlaybackHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>Denormalized from the item at open time so history queries can filter by
    /// type (video vs music) without joining.</summary>
    public MediaType MediaType { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last progress beat seen for this play. Drives the dedup window: a beat within
    /// the window continues this row, a later one starts a new play.</summary>
    public DateTime LastBeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>Furthest playback position (seconds) observed during this play.</summary>
    public double MaxPosition { get; set; }

    public bool Completed { get; set; }
}
