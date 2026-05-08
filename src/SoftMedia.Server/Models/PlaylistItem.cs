using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// A single ordered slot inside a <see cref="Playlist"/>. Wave E1.
///
/// Surrogate <see cref="Id"/> rather than a composite (PlaylistId, MediaItemId)
/// PK — duplicates are allowed by design: a user can put the same track in
/// their playlist twice (intentional repeat). Reorder operations therefore
/// permute by <see cref="Id"/>, not by <see cref="MediaItemId"/>.
/// </summary>
[Index(nameof(PlaylistId), nameof(Order))]
public class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>Zero-based dense order within the playlist.</summary>
    public int Order { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
