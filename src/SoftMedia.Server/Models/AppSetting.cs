using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public class AppSetting
{
    [Key]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Group { get; set; } = "General"; // e.g., "Server", "Playback", "Metadata"
}
