namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Result of processing a single file during a library scan.
/// </summary>
public enum ScanResult
{
    /// <summary>A new item was added to the library.</summary>
    New,
    
    /// <summary>An existing item was updated with new information.</summary>
    Updated,
    
    /// <summary>The item was skipped (no changes needed).</summary>
    Skipped
}
