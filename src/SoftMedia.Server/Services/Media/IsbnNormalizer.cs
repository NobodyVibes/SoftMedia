namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Canonical form for ISBNs so the value stored on <see cref="Models.MediaItem.Isbn"/> is
/// identical no matter which source supplied it. EPUB OPF identifiers arrive in every
/// imaginable shape ("urn:isbn:978-0-316-76948-8", "ISBN 0316769487", "978 0316769488")
/// while OpenLibrary returns bare digits — without a single normaliser the two paths would
/// disagree on the same book and the file-wins precedence rule in MetadataAggregator would
/// be comparing apples to oranges.
/// </summary>
public static class IsbnNormalizer
{
    /// <summary>
    /// Strips every character that is not a digit or a trailing ISBN-10 'X' check digit and
    /// returns the result only when it is a plausible ISBN-10 or ISBN-13. Returns
    /// <c>null</c> for anything else — a UUID, a URN, or a publisher's internal SKU must not
    /// end up displayed as an ISBN. Check digits are NOT validated: real-world catalogues
    /// carry a small number of mistyped-but-correct-length ISBNs, and rejecting them would
    /// lose more good data than it filters bad.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var buffer = new System.Text.StringBuilder(13);
        foreach (var c in raw)
        {
            if (char.IsDigit(c))
            {
                buffer.Append(c);
            }
            else if (c is 'X' or 'x')
            {
                // 'X' is only ever the ISBN-10 check digit, i.e. the 10th character. Anything
                // earlier means we are looking at prose ("Text ISBN..."), not an identifier.
                if (buffer.Length != 9) return null;
                buffer.Append('X');
            }
        }

        var digits = buffer.ToString();
        return digits.Length is 10 or 13 ? digits : null;
    }

    /// <summary>
    /// True when <paramref name="raw"/> normalises to a usable ISBN. Kept alongside
    /// <see cref="Normalize"/> so callers that only need a predicate read clearly.
    /// </summary>
    public static bool LooksLikeIsbn(string? raw) => Normalize(raw) != null;
}
