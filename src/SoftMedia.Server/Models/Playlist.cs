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

    /// <summary>
    /// Manual (stored <see cref="Items"/>) or Smart (membership derived from
    /// <see cref="SmartRules"/> on every read). Existing rows default to Manual.
    /// </summary>
    public PlaylistKind Kind { get; set; } = PlaylistKind.Manual;

    /// <summary>
    /// Serialized <see cref="SmartPlaylistRules"/>; null for manual playlists.
    ///
    /// Stored as JSON rather than broken out into columns because the rule set is
    /// one optional-heavy value object that is always read and written whole — a
    /// column per filter would add a migration every time a filter is added, and
    /// a rules table would be a join for data that is never queried across
    /// playlists. Validated and canonicalised on write (see
    /// <see cref="SmartPlaylistRules.Validate"/> / Normalize) so readers can trust
    /// the shape.
    /// </summary>
    [MaxLength(2000)]
    public string? SmartRules { get; set; }

    /// <summary>
    /// Web path of an uploaded cover, or null to fall back to the mosaic built
    /// from the playlist's own tracks. Written only by
    /// <see cref="Services.Media.IPlaylistCoverService"/>, which derives the
    /// filename from <see cref="Id"/> — the client's filename never reaches disk.
    /// </summary>
    [MaxLength(400)]
    public string? CoverImagePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Stored membership. Always empty for <see cref="PlaylistKind.Smart"/>.</summary>
    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
}
