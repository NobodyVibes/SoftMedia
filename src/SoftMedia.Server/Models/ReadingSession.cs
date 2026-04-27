namespace SoftMedia.Server.Models;

/// <summary>
/// One discrete period a user spent in the reader with a specific book open
/// (ER-052). Client instruments session start on reader mount and end on
/// unmount <em>or</em> idle timeout — so a user who walks away mid-chapter
/// doesn't show a 10-hour session next time they open the stats.
///
/// <see cref="PagesRead"/> is the delta of pages (PDF/CBZ) or spread hops
/// (EPUB approximation) between start and end. Zero-activity idle-timeout
/// sessions are discarded client-side rather than persisted.
/// </summary>
public class ReadingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null while the session is still running. Set by the end endpoint.</summary>
    public DateTime? EndedAt { get; set; }

    public int PagesRead { get; set; }
}
