using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Wave E2 — a movie collection / franchise (e.g. "The Lord of the Rings",
/// "John Wick", "Tarantino films"). Two flavours sit in this single table,
/// distinguished by <see cref="WikidataId"/>:
///
///   - Auto-collections — populated during metadata enrichment from
///     Wikidata's <c>wdt:P179</c> ("part of the series") property.
///     <see cref="WikidataId"/> is the series QID. Auto rows are read-only
///     to admins and users; if a user wants a different grouping they
///     create a manual collection alongside.
///
///   - Manual collections — admin-curated. <see cref="WikidataId"/> is null.
///     Admin-only mutation endpoints add/remove movies.
///
/// Threshold semantics (research finding from Plex defaults): the strip
/// view in MovieDetailView only renders when ≥2 visible siblings exist.
/// The home-page row only shows collections with ≥2 visible items. Both
/// rules are applied at *read* time so per-user ACL is honoured.
/// </summary>
[Index(nameof(WikidataId), IsUnique = true)]
public class Collection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Overview { get; set; }

    /// <summary>
    /// Remote poster URL (auto-collections inherit Wikidata's image; manual
    /// collections can have null and the UI falls back to the first movie's
    /// poster).
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Wikidata QID of the parent series (e.g. "Q170461" for LOTR), or null
    /// for manual collections. Filtered-unique constraint enforces "one
    /// auto-collection per series" while allowing many manual collections
    /// to coexist.
    /// </summary>
    [MaxLength(32)]
    public string? WikidataId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaItem> Items { get; set; } = new List<MediaItem>();
}
