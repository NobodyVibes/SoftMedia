namespace SoftMedia.Server.DTOs;

/// <summary>
/// Request to update user preferences.
/// </summary>
public record UpdatePreferencesRequest(Dictionary<string, string> Preferences);
