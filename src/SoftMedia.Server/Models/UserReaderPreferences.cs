using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// Per-(user, book) reader preference overrides. Layered on top of the global
/// reader defaults the client holds in localStorage — when a row exists for a
/// given (UserId, MediaItemId), the reader hydrates with global defaults first
/// then overlays any present field from this row.
///
/// Payload is stored as an opaque JSON blob rather than a column-per-field so
/// adding a new reader preference never requires a migration. The client is
/// the source of truth for the payload's schema; <see cref="SchemaVersion"/>
/// lets the server advise a reset when the client ships a newer shape than the
/// row was written under.
/// </summary>
public class UserReaderPreferences
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>
    /// Payload version. Always written by the client, read by the client on
    /// load. Server treats this as opaque metadata — no server-side decoding
    /// of the preferences payload, so the server never needs a schema bump
    /// when the client extends the preference shape.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Serialised JSON of the overrides. Partial shape — only fields the user
    /// has explicitly saved as book-specific overrides are included. Size
    /// capped to 8 KB so a malicious / malformed client can't write large
    /// blobs into the row.
    /// </summary>
    [MaxLength(8192)]
    public string PreferencesJson { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
