namespace SoftMedia.Server.Services;

/// <summary>
/// Service for managing per-user preferences stored on the server.
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>
    /// Gets all preferences for a user as a dictionary.
    /// </summary>
    Task<Dictionary<string, string>> GetPreferencesAsync(Guid userId);

    /// <summary>
    /// Gets a single preference value, returning defaultValue if not found.
    /// </summary>
    Task<string> GetPreferenceAsync(Guid userId, string key, string defaultValue);

    /// <summary>
    /// Sets a single preference value.
    /// </summary>
    Task SetPreferenceAsync(Guid userId, string key, string value);

    /// <summary>
    /// Sets multiple preference values at once.
    /// </summary>
    Task SetPreferencesAsync(Guid userId, Dictionary<string, string> preferences);

    /// <summary>
    /// Initializes default preferences for a new user.
    /// </summary>
    Task InitializeDefaultsAsync(Guid userId);
}
