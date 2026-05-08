using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// A user-owned, persisted ordered list of media items. Wave E1.
///
/// Privacy: <see cref="IsPublic"/> defaults to <c>false</c>. Research finding:
/// Jellyfin filed "playlists public by default" as a bug — users on shared
/// servers clobber each other's lists when the default is open. SoftMedia
/// inverts this: explicit opt-in to share.
///
/// Scope: v1 holds audio tracks only (MediaType.Audio). The PlaylistsController
/// validates this on add. Movie / show playlists are deferred — kept the schema
/// type-agnostic so the future expansion is a controller-only change.
/// </summary>
[Index(nameof(OwnerUserId))]
public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
}
