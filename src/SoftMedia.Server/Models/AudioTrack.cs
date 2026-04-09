using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public class AudioTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Foreign Key
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    
    public int Index { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
}
