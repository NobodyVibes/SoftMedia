namespace SoftMedia.Server.Helpers;

/// <summary>
/// Guards against ffmpeg/ffprobe ARGUMENT injection (security audit H2/M2).
///
/// Media file paths are handed to external processes (ffmpeg/ffprobe) whose command lines
/// are, in several builders, assembled as interpolated strings such as <c>-i "{path}"</c>.
/// With <c>UseShellExecute=false</c> there is no OS shell, but .NET still re-tokenizes the
/// <c>Arguments</c> string into argv before exec. On Linux a filename may legally contain a
/// double-quote or control character, which can close the quoted <c>-i</c> token early and
/// inject additional ffmpeg options (arbitrary file read/write as the server account).
///
/// We reject such paths at the trust boundary — at scan time (so a hostile name never enters
/// the library) and again before any user-facing process spawn (via StreamSecurityService).
/// The belt-and-suspenders fix is <c>ProcessStartInfo.ArgumentList</c> (per-token, no string
/// re-parsing); this guard closes the live vector everywhere until that migration is complete.
/// </summary>
public static class MediaPathSafety
{
    /// <summary>
    /// True if the path contains a character that could break out of a quoted process
    /// argument: a double quote, or any control character (NUL, newline, CR, tab, etc.).
    /// Legitimate media filenames effectively never contain these.
    /// </summary>
    public static bool HasArgumentInjectionRisk(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var c in path)
        {
            if (c == '"' || char.IsControl(c)) return true;
        }
        return false;
    }
}
