using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// A user-saved highlight inside a book. Location shape is format-specific and
/// opaque to the server: EPUB stores a CFI range, PDF stores page + rect info
/// as JSON. Keeping the structure inside <see cref="LocationJson"/> means a new
/// format or a richer range type never needs a migration.
///
/// <see cref="QuotedText"/> is stored server-side so the highlight list /
/// Markdown export can render the quote without re-opening the source file.
/// <see cref="Note"/> is ER-041's addition; the column ships with ER-040 so the
/// notes feature is purely an additive UI change.
/// </summary>
public class Highlight
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>
    /// Opaque JSON owned by the client. Expected shape:
    ///   EPUB: { "type": "epub", "cfi": "epubcfi(...)" }
    ///   PDF:  { "type": "pdf", "page": N, "rects": [{x,y,w,h}, ...] }
    /// The server neither parses nor validates the blob beyond size.
    /// </summary>
    [MaxLength(4096)]
    public string LocationJson { get; set; } = "{}";

    /// <summary>CSS-safe colour token. Free-form string to give the client room to
    /// iterate on the palette without schema pressure; capped at a reasonable
    /// length. Validated client-side against the palette the user sees.</summary>
    [MaxLength(32)]
    public string Colour { get; set; } = "yellow";

    /// <summary>The quoted passage. Long fields allowed — users quote paragraphs.</summary>
    [MaxLength(8192)]
    public string QuotedText { get; set; } = string.Empty;

    /// <summary>ER-041: optional note attached to the highlight. 8 KB cap; anything
    /// longer suggests a different tool is needed.</summary>
    [MaxLength(8192)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
