namespace SoftMedia.Server.Models;

/// <summary>
/// System notification for admin alerts (API limits, scan failures, etc.)
/// Designed to be extensible for future notification types.
/// </summary>
public class SystemNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Notification type for categorization (e.g., "api_exhausted", "scan_failed")
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    public string Title { get; set; } = string.Empty;
    
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Severity level: "info", "warning", "error"
    /// </summary>
    public string Severity { get; set; } = "info";
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the notification was dismissed. Null if still active.
    /// </summary>
    public DateTime? DismissedAt { get; set; }
    
    /// <summary>
    /// Username of admin who dismissed this notification.
    /// </summary>
    public string? DismissedBy { get; set; }
    
    /// <summary>
    /// Optional JSON metadata for type-specific data.
    /// </summary>
    public string? Metadata { get; set; }
}
