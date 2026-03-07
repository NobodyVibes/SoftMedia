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

    /// <summary>
    /// Calculates the Levenshtein distance between two strings.
    /// </summary>
    public static int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return string.IsNullOrEmpty(target) ? 0 : target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        int n = source.Length;
        int m = target.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }
}
