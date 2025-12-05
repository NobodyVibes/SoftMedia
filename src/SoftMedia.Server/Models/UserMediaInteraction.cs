using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

public class UserMediaInteraction
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    [Range(1, 5)]
    public int? Rating { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsWatched { get; set; }

    public DateTime? LastPlayed { get; set; }
}
