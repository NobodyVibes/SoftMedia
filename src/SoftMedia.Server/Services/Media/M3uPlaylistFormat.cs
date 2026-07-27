using System.Text;

namespace SoftMedia.Server.Services.Media;

/// <summary>One track's worth of the information an M3U can carry.</summary>
public record M3uTrack(string Path, string? Title, string? Artist, int DurationSeconds);

/// <summary>
/// Reading and writing extended M3U, the lingua franca of playlist interchange
/// (VLC, foobar2000, Kodi, Rhythmbox all speak it).
///
/// Pure string handling with no filesystem access: import never opens a path it
/// was given, it only matches the text against rows already in the library, so a
/// crafted playlist cannot be used to probe the disk.
///
/// Encoding is UTF-8 without a BOM. The `.m3u8` extension is the formal marker
/// for UTF-8 M3U, but in this codebase that extension means an HLS manifest
/// everywhere else, so exports use `.m3u` and rely on UTF-8 being what every
/// current player assumes.
/// </summary>
public static class M3uPlaylistFormat
{
    /// <summary>Refuse to parse anything larger; an import is text, not a file dump.</summary>
    public const int MaxContentBytes = 2 * 1024 * 1024;

    /// <summary>Upper bound on entries taken from one imported file.</summary>
    public const int MaxEntries = 5000;

    public static string Write(string playlistName, IEnumerable<M3uTrack> tracks)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");

        // #PLAYLIST is the de-facto way to carry the list's own name; players that
        // don't know it treat the line as a comment.
        if (!string.IsNullOrWhiteSpace(playlistName))
            sb.Append("#PLAYLIST:").Append(Sanitize(playlistName)).Append('\n');

        foreach (var track in tracks)
        {
            var label = string.IsNullOrWhiteSpace(track.Artist)
                ? Sanitize(track.Title ?? string.Empty)
                : $"{Sanitize(track.Artist)} - {Sanitize(track.Title ?? string.Empty)}";

            // A negative or unknown duration is written as -1, which is the
            // convention for "stream of unknown length".
            var seconds = track.DurationSeconds > 0 ? track.DurationSeconds : -1;
            sb.Append("#EXTINF:").Append(seconds).Append(',').Append(label).Append('\n');
            sb.Append(track.Path).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Path lines from an M3U, in order, with directives and blanks dropped.
    /// Duplicates are preserved: a playlist that deliberately repeats a track
    /// should import as that same repetition.
    /// </summary>
    public static List<string> ParsePaths(string content)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return paths;

        // Accept CRLF, LF and lone-CR line endings — playlists travel between
        // Windows, Linux and old Mac tooling.
        foreach (var raw in content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue; // #EXTM3U, #EXTINF, #PLAYLIST, comments

            paths.Add(line);
            if (paths.Count >= MaxEntries) break;
        }

        return paths;
    }

    /// <summary>The playlist's own name if the file declares one via #PLAYLIST.</summary>
    public static string? ParseName(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        foreach (var raw in content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase)) continue;

            var name = line["#PLAYLIST:".Length..].Trim();
            return name.Length == 0 ? null : name;
        }

        return null;
    }

    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>
    /// Final path segment, for matching a playlist written on another machine
    /// where the library sits at a different mount point.
    /// </summary>
    public static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var slash = trimmed.LastIndexOfAny(Separators);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>
    /// The last two segments, separator-normalised — "Album/01.mp3".
    ///
    /// The discriminating half of filename matching. Track files are routinely
    /// named "01.mp3" or "track01.flac" in every album folder, so a bare filename
    /// is far too weak to identify a track on its own; including the parent
    /// directory makes a match meaningful while still surviving the change of
    /// mount point that filename matching exists to handle.
    /// </summary>
    public static string TailOf(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var slash = trimmed.LastIndexOfAny(Separators);
        if (slash < 0) return trimmed;

        var parentEnd = slash;
        var parentStart = trimmed.LastIndexOfAny(Separators, parentEnd - 1 < 0 ? 0 : parentEnd - 1);
        var tail = parentStart >= 0 ? trimmed[(parentStart + 1)..] : trimmed;
        return tail.Replace('\\', '/');
    }

    /// <summary>
    /// Strips newlines from a value destined for a single-line directive, so a
    /// track title containing a line break cannot forge extra M3U lines.
    /// </summary>
    private static string Sanitize(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
