using System.Text.RegularExpressions;

namespace SoftMedia.Server.Helpers;

public static class MediaStringHelpers
{
    /// <summary>
    /// Generates a sortable title by removing common articles (The, A, An).
    /// </summary>
    public static string GetSortTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        if (title.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            return title[4..];
        if (title.StartsWith("A ", StringComparison.OrdinalIgnoreCase))
            return title[2..];
        if (title.StartsWith("An ", StringComparison.OrdinalIgnoreCase))
            return title[3..];
            
        return title;
    }
}
