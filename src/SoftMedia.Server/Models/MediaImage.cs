using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

public class MediaImage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid MediaItemId { get; set; }
    
    [ForeignKey(nameof(MediaItemId))]
    public MediaItem? MediaItem { get; set; }

    [Required]
    public string ImageType { get; set; } = "Poster"; // Poster, Backdrop

    [Required]
    public string MimeType { get; set; } = "image/jpeg";

    [Required]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
