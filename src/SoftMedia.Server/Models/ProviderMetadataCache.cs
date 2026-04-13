using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

/// <summary>
/// Caches raw, provider-specific metadata payloads (like JSON) locally.
/// This prevents losing bulk data (e.g. TVMaze full series payload) without bloating the MediaItem schema.
/// </summary>
public class ProviderMetadataCache
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid MediaItemId { get; set; }

    [ForeignKey(nameof(MediaItemId))]
    public MediaItem MediaItem { get; set; } = null!;

    /// <summary>
    /// Identifier for the provider (e.g., "TVMaze", "OMDb", "Embedded").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// The raw payload string, typically JSON.
    /// </summary>
    [Required]
    public string RawPayload { get; set; } = string.Empty;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
