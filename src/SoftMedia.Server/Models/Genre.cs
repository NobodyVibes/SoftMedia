using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Normalized genre entity replacing the comma-separated string in MediaItem.Genres.
/// Enables indexed genre filtering via junction table instead of LIKE '%Action%' queries.
/// </summary>
[Index(nameof(Name), IsUnique = true)]
public class Genre
{
    public int Id { get; set; }

    /// <summary>Genre name (e.g., "Action", "Drama", "Sci-Fi").</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}
