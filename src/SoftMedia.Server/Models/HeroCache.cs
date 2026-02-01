using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public class HeroCache
{
    [Key]
    public int Id { get; set; } = 1; // Singleton ID

    public string CachedJson { get; set; } = string.Empty;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
