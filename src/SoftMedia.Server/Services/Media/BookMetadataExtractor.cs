using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SoftMedia.Server.Services.Abstractions;
using UglyToad.PdfPig;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Reads publisher-embedded metadata directly out of EPUB and PDF files.
/// EPUBs carry a full Dublin Core metadata block in their OPF package, so
/// we consistently get clean Title/Author/Publisher/Year/ISBN fields. PDFs
/// expose the Info dictionary via PdfPig; quality there depends on whoever
/// produced the file (scanned PDFs often have nothing).
/// </summary>
public sealed class BookMetadataExtractor : IBookMetadataExtractor
{
    private readonly ILogger<BookMetadataExtractor> _logger;

    // OPF uses Dublin Core — the dc namespace is constant across the spec.
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";

    // Audit L5: cap the decompressed XML size and forbid DTDs/external entities so a small
    // EPUB whose container.xml/OPF inflates to a huge tree can't exhaust memory during a scan.
    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 4_000_000, // ~4 MiB of XML text — generous for an OPF manifest
    };

    private static XDocument LoadXmlCapped(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, SafeXmlSettings);
        return XDocument.Load(reader);
    }

    public BookMetadataExtractor(ILogger<BookMetadataExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<BookFileMetadata?> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".epub" => await Task.Run(() => ExtractEpub(filePath), cancellationToken),
                ".pdf" => await Task.Run(() => ExtractPdf(filePath), cancellationToken),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // Embedded-metadata extraction is best-effort. A malformed file must never
            // take down the scan — we fall back to filename parsing.
            _logger.LogWarning(ex, "[BookMetadataExtractor] Failed to extract metadata from {Path}", filePath);
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────── EPUB

    private BookFileMetadata? ExtractEpub(string filePath)
    {
        using var zip = ZipFile.OpenRead(filePath);

        // The OPF path isn't fixed — container.xml points at it.
        var containerEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), "META-INF/container.xml",
                StringComparison.OrdinalIgnoreCase));
        if (containerEntry == null) return null;

        string opfPath;
        {
            var containerDoc = LoadXmlCapped(containerEntry);
            var rootfile = containerDoc.Descendants(Container + "rootfile").FirstOrDefault()
                          ?? containerDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
            opfPath = rootfile?.Attribute("full-path")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(opfPath)) return null;
        }

        var opfEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), opfPath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase));
        if (opfEntry == null) return null;

        var opf = LoadXmlCapped(opfEntry);

        // Some publishers omit the dc namespace prefix or use a different one —
        // match on LocalName so we catch both "<dc:title>" and "<title>".
        string? FindDc(string local) =>
            opf.Descendants().FirstOrDefault(e =>
                e.Name.LocalName.Equals(local, StringComparison.OrdinalIgnoreCase) &&
                (e.Name.Namespace == Dc || e.Name.Namespace == OpfNs || e.Name.Namespace == XNamespace.None))
                ?.Value?.Trim();

        var title = FindDc("title");
        var author = NormalizeCreator(FindDc("creator"));
        var publisher = FindDc("publisher");
        var description = FindDc("description");
        var language = FindDc("language");
        var rawDate = FindDc("date");
        int? year = ExtractYear(rawDate);

        // <dc:identifier opf:scheme="ISBN">978-...</dc:identifier>
        var isbn = opf.Descendants()
            .Where(e => e.Name.LocalName.Equals("identifier", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value?.Trim() ?? string.Empty)
            .FirstOrDefault(v => LooksLikeIsbn(v));

        return new BookFileMetadata(
            Title: string.IsNullOrWhiteSpace(title) ? null : title,
            Author: string.IsNullOrWhiteSpace(author) ? null : author,
            Year: year,
            Publisher: string.IsNullOrWhiteSpace(publisher) ? null : publisher,
            Description: string.IsNullOrWhiteSpace(description) ? null : StripHtml(description),
            Isbn: string.IsNullOrWhiteSpace(isbn) ? null : NormalizeIsbn(isbn),
            Language: string.IsNullOrWhiteSpace(language) ? null : language
        );
    }

    // ──────────────────────────────────────────────────────────────────────── PDF

    private BookFileMetadata? ExtractPdf(string filePath)
    {
        // PdfPig is strictly synchronous and can throw on encrypted / malformed PDFs.
        // The outer try/catch in ExtractAsync handles those.
        using var doc = PdfDocument.Open(filePath);
        var info = doc.Information;
        if (info == null) return null;

        var title = string.IsNullOrWhiteSpace(info.Title) ? null : info.Title.Trim();
        var author = NormalizeCreator(info.Author);
        int? year = ExtractYear(info.CreationDate);

        // Reject the common "default" titles that stock PDF writers stamp in — those
        // are worse than the filename because they trick the downstream provider
        // into searching for "Microsoft Word - Document1".
        if (title != null && IsJunkPdfTitle(title))
        {
            title = null;
        }

        if (title == null && author == null && !year.HasValue)
            return null;

        return new BookFileMetadata(
            Title: title,
            Author: author,
            Year: year,
            Publisher: null,
            Description: null,
            Isbn: null,
            Language: null
        );
    }

    // ───────────────────────────────────────────────────────────────────── helpers

    /// <summary>
    /// EPUB OPF and PDF Info-dict authors frequently use the library-catalog
    /// "Lastname, Firstname" convention (common in Sigil/Calibre rips) — e.g.
    /// <c>&lt;dc:creator&gt;King, Stephen&lt;/dc:creator&gt;</c>. OpenLibrary's
    /// <c>author=</c> Solr field expects "First Last" and its indexer does NOT
    /// auto-flip comma-inverted names. Passing the comma-form verbatim returns
    /// zero docs for most queries, which was silently failing ~60 books in the
    /// Stephen King collection. We flip on the FIRST comma only, which handles
    /// single authors plus the first entry of a comma-separated co-author list.
    /// </summary>
    private static string? NormalizeCreator(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        // Heuristic: if there's a single comma and both sides look like
        // capitalised name pieces (no digits, no extra punctuation), flip it.
        // Multi-comma strings are already co-author lists and left as-is; the
        // provider splits on commas downstream.
        var firstComma = trimmed.IndexOf(',');
        if (firstComma > 0 && trimmed.IndexOf(',', firstComma + 1) < 0)
        {
            var last = trimmed.Substring(0, firstComma).Trim();
            var first = trimmed.Substring(firstComma + 1).Trim();
            if (IsNamePiece(last) && IsNamePiece(first))
            {
                return $"{first} {last}";
            }
        }
        return trimmed;
    }

    private static bool IsNamePiece(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 60) return false;
        foreach (var c in s)
        {
            if (char.IsLetter(c) || char.IsWhiteSpace(c) || c == '.' || c == '\'' || c == '-')
                continue;
            return false;
        }
        return char.IsUpper(s[0]);
    }

    private static int? ExtractYear(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Covers both "2012-03-14", "2012", and PDF's "D:20120314..." form.
        var m = Regex.Match(raw, @"(?<!\d)(19|20)\d{2}(?!\d)");
        if (m.Success && int.TryParse(m.Value, out var y) && y >= 1900 && y <= 2100)
            return y;
        return null;
    }

    private static bool LooksLikeIsbn(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 10 || digits.Length == 13;
    }

    private static string NormalizeIsbn(string value)
    {
        var digits = new string(value.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray());
        return digits;
    }

    private static string StripHtml(string html)
    {
        // Publishers sometimes stuff HTML markup into <dc:description>. Drop tags so
        // the text fits a plain overview field without surprise formatting.
        var text = Regex.Replace(html, "<[^>]+>", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool IsJunkPdfTitle(string title)
    {
        // Patterns that real books never use but PDF generators love.
        return title.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("untitled", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(title, @"^document\d*$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(title, @"^\s*$");
    }
}
