namespace SoftMedia.Server.Helpers;

/// <summary>
/// SM-WI-056 — file-path comparison that matches the host filesystem's semantics.
/// Windows/macOS paths are case-insensitive; Linux paths are case-sensitive, where an
/// OrdinalIgnoreCase comparer silently merges genuinely distinct files ("Movie.mkv" vs
/// "movie.mkv") onto one row. Use for every path-keyed cache/set on the scan paths.
/// </summary>
public static class PathComparers
{
    public static readonly StringComparer Platform =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static readonly StringComparison PlatformComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
