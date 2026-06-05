namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Defines the scope of metadata refresh during scanning or manual updates.
/// </summary>
public enum MetadataRefreshMode
{
    /// <summary>
    /// No analysis or enrichment. Validation only (file exists).
    /// </summary>
    None,

    /// <summary>
    /// Analyze and enrich only if metadata is missing or file has been modified.
    /// This is the default mode for regular scans.
    /// </summary>
    Missing,

    /// <summary>
    /// Force re-analysis and re-enrichment regardless of current state.
    /// Used for manual "Refresh Metadata" requests.
    /// </summary>
    Full
}
