using System.Text.Json;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Utility class for safe, consistent parsing of MetadataJson strings.
/// Eliminates repeated try/catch + Deserialize patterns across the codebase.
/// </summary>
public static class MetadataJsonHelper
{
    private static readonly Dictionary<string, object> EmptyDict = new();

    /// <summary>
    /// Parse MetadataJson string to dictionary. Returns empty dict if null, empty, or invalid JSON.
    /// </summary>
    public static Dictionary<string, object> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Safely get a string value from parsed metadata.
    /// Handles both raw strings and JsonElement values.
    /// </summary>
    public static string? GetString(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is JsonElement el)
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();

        return value.ToString();
    }

    /// <summary>
    /// Safely get an int value from parsed metadata.
    /// Handles JsonElement (Number or String) and raw int/string values.
    /// </summary>
    public static int? GetInt(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var intVal))
                return intVal;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
                return parsed;
            return null;
        }

        return int.TryParse(value.ToString(), out var result) ? result : null;
    }

    /// <summary>
    /// Safely get a double value from parsed metadata.
    /// </summary>
    public static double? GetDouble(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var dblVal))
                return dblVal;
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var parsed))
                return parsed;
            return null;
        }

        return double.TryParse(value.ToString(), out var result) ? result : null;
    }

    /// <summary>
    /// Safely get a DateTime value from parsed metadata.
    /// </summary>
    public static DateTime? GetDateTime(Dictionary<string, object> meta, string key)
    {
        var str = GetString(meta, key);
        return DateTime.TryParse(str, out var dt) ? dt : null;
    }

    /// <summary>
    /// Serialize a metadata dictionary back to JSON string.
    /// </summary>
    public static string Serialize(Dictionary<string, object> meta)
    {
        return JsonSerializer.Serialize(meta);
    }
}
