using System.Text.Json;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Provides non-destructive merge operations for MetadataJson dictionaries.
/// Preserves type fidelity by operating on <see cref="JsonElement"/> values
/// instead of <c>object</c> to avoid lossy boxing/unboxing.
/// </summary>
public static class MetadataJsonMerger
{
    /// <summary>
    /// Merges <paramref name="incoming"/> values into <paramref name="existing"/>.
    /// Only non-null values from <paramref name="incoming"/> overwrite existing entries.
    /// Keys in <paramref name="existing"/> that are absent from <paramref name="incoming"/> are preserved.
    /// </summary>
    /// <param name="existing">The base dictionary (mutated in place and returned).</param>
    /// <param name="incoming">New values to merge. Null/Undefined values are skipped.</param>
    /// <returns>The merged dictionary (same reference as <paramref name="existing"/>).</returns>
    public static Dictionary<string, JsonElement> Merge(
        Dictionary<string, JsonElement> existing,
        Dictionary<string, JsonElement> incoming)
    {
        foreach (var kvp in incoming)
        {
            // Skip null and undefined values — don't overwrite good data with nothing
            if (kvp.Value.ValueKind == JsonValueKind.Null ||
                kvp.Value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            existing[kvp.Key] = kvp.Value;
        }

        return existing;
    }

    /// <summary>
    /// Convenience overload that parses two JSON strings, merges them, and re-serializes.
    /// </summary>
    /// <param name="existingJson">Existing MetadataJson (may be null/empty).</param>
    /// <param name="incomingJson">New metadata JSON to merge in.</param>
    /// <returns>Merged JSON string.</returns>
    public static string MergeJson(string? existingJson, string incomingJson)
    {
        var existing = MetadataJsonHelper.ParseElements(existingJson);
        var incoming = MetadataJsonHelper.ParseElements(incomingJson);

        var merged = Merge(existing, incoming);
        return JsonSerializer.Serialize(merged);
    }
}
