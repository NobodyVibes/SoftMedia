using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// A user-created bookmark inside a book. One book can have many bookmarks
/// per user; they survive book re-scans because the identity is
/// <c>(UserId, MediaItemId, Id)</c> — file-path changes never touch these rows.
///
/// Location is represented two ways depending on format:
/// - <see cref="Position"/> holds a 1-based page number for PDF and comic archives.
/// - <see cref="Cfi"/> holds a CFI (Canonical Fragment Identifier) for EPUB.
/// Exactly one of the two is populated. A bookmark with neither is invalid and
/// must be rejected at the controller.
/// </summary>
public class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>1-based page number for PDF / CBZ / CBR. Null for EPUB.</summary>
    public int? Position { get; set; }

    /// <summary>EPUB CFI. Null for paginated formats.</summary>
    [MaxLength(512)]
    public string? Cfi { get; set; }

    /// <summary>Optional user-supplied label. Truncated at 200 chars.</summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
