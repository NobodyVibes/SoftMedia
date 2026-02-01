using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

public class LibraryRecentCache
{
    [Key]
    public Guid LibraryId { get; set; }
    
    [ForeignKey(nameof(LibraryId))]
    public Library Library { get; set; } = null!;

    public string CachedJson { get; set; } = string.Empty;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
