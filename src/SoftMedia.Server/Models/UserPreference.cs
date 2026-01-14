namespace SoftMedia.Server.Models;

/// <summary>
/// Per-user preference settings stored on the server for cross-device consistency.
/// </summary>
public class UserPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The user who owns this preference.
    /// </summary>
    public Guid UserId { get; set; }
    
    /// <summary>
    /// Preference key (e.g., "Language", "SubtitleLanguage").
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// Preference value as string.
    /// </summary>
    public string Value { get; set; } = string.Empty;
    
    /// <summary>
    /// Last time this preference was updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Navigation property to User.
    /// </summary>
    public User? User { get; set; }
}
