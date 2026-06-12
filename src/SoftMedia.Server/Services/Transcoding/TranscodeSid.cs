using System.Text.RegularExpressions;

namespace SoftMedia.Server.Services.Transcoding;

/// <summary>
/// Validates the client-supplied transcode session id (<c>?sid=</c>). The sid is concatenated
/// into the on-disk session directory name (<see cref="TranscodeService.GetSessionDir"/>) and used
/// as part of the session key, so an unvalidated value is a directory-traversal sink and a
/// session/lock-table growth vector (security audit wave-2 M-4). A legitimate sid is a short
/// opaque token the SPA generates (UUID / random alphanumeric); we constrain it to exactly that.
/// </summary>
public static partial class TranscodeSid
{
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SidPattern();

    /// True when <paramref name="sid"/> is null/empty (the "no explicit session" case) or matches
    /// the safe charset/length. False for anything that could traverse or bloat the session space.
    public static bool IsValid(string? sid) =>
        string.IsNullOrEmpty(sid) || SidPattern().IsMatch(sid);
}
