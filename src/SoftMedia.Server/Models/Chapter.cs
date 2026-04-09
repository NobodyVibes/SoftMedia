using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Foreign Key
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    
    public double StartTime { get; set; }
    public string Title { get; set; } = string.Empty;
}
