using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

public class OpenLibraryProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenLibraryProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IBookMetadataExtractor? _bookMetadataExtractor;

    /// <summary>
    /// Maximum score (lower is better) we'll accept as a confident match. An exact
    /// title hit with author and cover scores 0; a one-character title diff with
    /// matching year and author still scores ~10. Anything above 100 means we're
    /// looking at a different book with the same word or two — better to return
    /// null and let the user stay unenriched than to stamp the wrong cover.
    /// </summary>
    private const int PoorMatchThreshold = 100;

    public LibraryType SupportedType => LibraryType.Book;
    public string ProviderName => "Open Library";

    public OpenLibraryProvider(
        HttpClient httpClient,
        ILogger<OpenLibraryProvider> logger,
        RateLimiterFactory rateLimiterFactory,
        IBookMetadataExtractor? bookMetadataExtractor = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("OpenLibrary");
        _bookMetadataExtractor = bookMetadataExtractor;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;

        // Guard: if the "title" is actually just the author's name (filename had no " - "
        // separator so ParseBook fell through, e.g. "Stephen King.epub"), there's nothing
        // useful to search for. Don't burn a retry slot on a guaranteed-empty query.
        if (string.IsNullOrWhiteSpace(title)
            || (!string.IsNullOrWhiteSpace(item.Director)
                && string.Equals(title.Trim(), item.Director.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("Skipping OpenLibrary lookup for '{Title}' — looks like an author-only filename", title);
            return null;
        }

        try
        {
            // Acquire rate limit lease (replaces manual SemaphoreSlim + delay)
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("OpenLibrary rate limit exceeded for '{Title}', skipping", title);
                return null;
            }

            // ISBN-first lookup — the authoritative match path. If the file is
            // an EPUB/PDF with a publisher-stamped ISBN, OpenLibrary's ISBN
            // field resolves to the exact edition; no title heuristics or
            // scoring needed. Falls through on extractor failure, missing ISBN,
            // or empty OL response.
            var isbnResult = await TryIsbnLookupAsync(item);
            if (isbnResult != null) return isbnResult;

            // Build search URL using structured params when author context is available.
            // OpenLibrary's search API supports title= and author= for more accurate results.
            // Use the promoted Director column for author context.
            // BookScanner stores parsed author in Director as the generic "primary creator" field.
            string? author = item.Director;

            // Normalise the outbound query title:
            //   (1) Strip leading articles ("A ", "An ", "The ") — OpenLibrary's
            //       Solr title index indexes "A Face in the Crowd" as "Face in
            //       the Crowd"; the article turns matches into zero-doc returns.
            //   (2) Replace non-alphanumeric characters (colons, slashes,
            //       underscores, em-dashes) with spaces. EPUB publisher titles
            //       commonly include "11/22/63: A Novel" or "Dolores Claiborne_
            //       A Novel"; passing those verbatim as `title=` burns the
            //       query on Solr punctuation handling.
            //   (3) Collapse whitespace.
            // item.Title on disk is left untouched; this is only for the URL.
            var queryTitle = Regex.Replace(
                title, @"^(?:A|An|The)\s+", "", RegexOptions.IgnoreCase);
            queryTitle = Regex.Replace(queryTitle, @"[^\p{L}\p{N}\s]", " ");
            queryTitle = Regex.Replace(queryTitle, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(queryTitle)) queryTitle = title;

            // Explicit field projection — OpenLibrary's default response payload is enormous
            // (all work/edition fields for every hit), which is a frequent cause of 500s and
            // slow responses under load. The docs recommend always passing `fields=`.
            // `edition_count` is added so the scorer can prefer canonical works over orphans.
            const string fields = "fields=key,title,author_name,first_publish_year,publisher,cover_i,isbn,subject,number_of_pages_median,edition_count";

            string url;
            if (!string.IsNullOrWhiteSpace(author))
            {
                // `sort=editions` asks OpenLibrary to return the most-published works first,
                // pushing canonical entries ahead of orphan reprints before we even score.
                url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(queryTitle)}&author={Uri.EscapeDataString(author)}&limit=10&sort=editions&{fields}";
            }
            else
            {
                url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(queryTitle)}&limit=10&sort=editions&{fields}";
            }

            var response = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("docs", out var docs) && docs.GetArrayLength() > 0)
            {
                // Materialise once — we need to do two passes (filter + score) over the same
                // result set and JsonElement enumeration is forward-only.
                var candidates = docs.EnumerateArray().ToList();

                // Pre-filter: when siblings have covers/authors, orphan entries are almost
                // always wrong and should be dropped before scoring. This directly addresses
                // cases like "Dune: House Atreides" where OpenLibrary returns an authorless,
                // coverless stub ahead of the real Herbert & Anderson entry.
                candidates = ApplySiblingFilters(candidates, author);
                if (candidates.Count == 0) return null;

                JsonElement? bestBook = null;
                int bestScore = int.MaxValue; // Lower is better

                foreach (var docEntry in candidates)
                {
                    int score = ScoreCandidate(docEntry, title, author, item.Year);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestBook = docEntry;
                    }
                }

                if (!bestBook.HasValue) return null;

                // Reject matches that scored poorly — a wrong-but-present result is worse
                // than no result, because it permanently poisons the detail page whereas
                // "no metadata" triggers another retry cycle that may do better. The
                // threshold is tuned so an exact match stays well under it.
                if (bestScore > PoorMatchThreshold)
                {
                    _logger.LogInformation(
                        "OpenLibrary best match for '{Title}' scored {Score} (> {Threshold}); rejecting as low-confidence",
                        title, bestScore, PoorMatchThreshold);
                    return null;
                }

                return MapDocToMetadata(bestBook.Value);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching OpenLibrary metadata for {Title}", title);
            return null;
        }
    }

    /// <summary>
    /// Authoritative ISBN-based lookup. Extracts the embedded ISBN from the
    /// book file (EPUB OPF <c>dc:identifier</c> / PDF Info dict), queries
    /// OpenLibrary's <c>search.json?isbn=</c> field, and maps the first hit
    /// directly to a <see cref="MetadataResult"/>. Returns <c>null</c> and
    /// lets the caller fall back to title/author search when anything goes
    /// wrong — the extractor being unavailable, the file having no ISBN, the
    /// ISBN not being in OL, or a transport error.
    /// </summary>
    private async Task<MetadataResult?> TryIsbnLookupAsync(MediaItem item)
    {
        if (_bookMetadataExtractor == null) return null;
        if (string.IsNullOrWhiteSpace(item.Path)) return null;

        BookFileMetadata? extracted;
        try
        {
            extracted = await _bookMetadataExtractor.ExtractAsync(item.Path);
        }
        catch
        {
            return null;
        }
        var isbn = extracted?.Isbn;
        if (string.IsNullOrWhiteSpace(isbn)) return null;

        try
        {
            const string fields = "fields=key,title,author_name,first_publish_year,publisher,cover_i,isbn,subject,number_of_pages_median,edition_count";
            var url = $"https://openlibrary.org/search.json?isbn={Uri.EscapeDataString(isbn)}&limit=1&{fields}";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("docs", out var docs)
                || docs.ValueKind != JsonValueKind.Array
                || docs.GetArrayLength() == 0)
            {
                return null;
            }

            _logger.LogInformation(
                "OpenLibrary ISBN lookup matched {Isbn} for '{Title}' — skipping heuristic search",
                isbn, item.Title);
            return MapDocToMetadata(docs[0]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "OpenLibrary ISBN lookup for {Isbn} failed; falling back to title/author search", isbn);
            return null;
        }
    }

    /// <summary>
    /// Turns an OpenLibrary search doc element into our <see cref="MetadataResult"/>.
    /// Shared between the ISBN-first and title/author paths since both paths
    /// receive the same doc shape.
    /// </summary>
    private static MetadataResult MapDocToMetadata(JsonElement book)
    {
        var result = new MetadataResult();

        if (book.TryGetProperty("title", out var titleProp)) result.Title = titleProp.GetString();
        if (book.TryGetProperty("first_publish_year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
            result.Year = yearProp.GetInt32();

        if (book.TryGetProperty("author_name", out var authors) && authors.ValueKind == JsonValueKind.Array)
        {
            result.Cast = authors.EnumerateArray()
                .Select(a => new CastMember { Name = a.GetString() ?? "Unknown", Character = "Author" })
                .ToList();
        }

        if (book.TryGetProperty("publisher", out var publishers)
            && publishers.ValueKind == JsonValueKind.Array
            && publishers.GetArrayLength() > 0)
        {
            var publisher = publishers[0].GetString();
            if (!string.IsNullOrEmpty(publisher))
            {
                result.Studio = publisher;
                result.Publisher = publisher;
            }
        }

        if (book.TryGetProperty("subject", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
        {
            result.Genres = subjects.EnumerateArray().Take(5).Select(s => s.GetString()!).ToList();
        }

        if (book.TryGetProperty("number_of_pages_median", out var pages) && pages.ValueKind == JsonValueKind.Number)
            result.PageCount = pages.GetInt32();

        if (book.TryGetProperty("isbn", out var isbns)
            && isbns.ValueKind == JsonValueKind.Array
            && isbns.GetArrayLength() > 0)
        {
            result.Isbn = isbns[0].GetString();
        }

        if (book.TryGetProperty("cover_i", out var coverId) && coverId.ValueKind == JsonValueKind.Number)
        {
            result.PosterUrl = $"https://covers.openlibrary.org/b/id/{coverId.GetInt32()}-L.jpg";
        }

        return result;
    }

    /// <summary>
    /// Drop orphan results (no cover, no author) when the result set has richer
    /// siblings to compare against. Also hard-filters by surname when we have
    /// a known author. If the filter would eliminate every candidate we fall
    /// back to the original list — partial data is better than none.
    /// </summary>
    private static List<JsonElement> ApplySiblingFilters(List<JsonElement> candidates, string? knownAuthor)
    {
        // Step 1: author surname filter. "Brian Herbert and Kevin J. Anderson" →
        // surnames {"Herbert", "Anderson"}; we keep any entry whose author_name
        // contains at least one of those surnames. The multi-surname approach
        // fixes cases where OL has the book attributed to a subset of the
        // co-authors — previously only matching the LAST surname meant entries
        // attributed solely to "Brian Herbert" were silently excluded.
        if (!string.IsNullOrWhiteSpace(knownAuthor))
        {
            var surnames = ExtractSurnames(knownAuthor);
            if (surnames.Count > 0)
            {
                var authorMatched = candidates.Where(c => AuthorListContainsAny(c, surnames)).ToList();
                if (authorMatched.Count > 0) candidates = authorMatched;
            }
        }

        // Step 2: if any candidate has a cover, drop the ones without. An exact
        // title match with no cover is almost always a reprint stub that doesn't
        // link back to the work record.
        if (candidates.Any(HasCover))
        {
            var withCovers = candidates.Where(HasCover).ToList();
            if (withCovers.Count > 0) candidates = withCovers;
        }

        // Step 3: same logic for authors — if any candidate has author_name,
        // drop the authorless ones. Orphan-with-title-only entries are junk.
        if (candidates.Any(HasAuthor))
        {
            var withAuthors = candidates.Where(HasAuthor).ToList();
            if (withAuthors.Count > 0) candidates = withAuthors;
        }

        return candidates;
    }

    /// <summary>
    /// Scores a single candidate. Lower is better. Zero is a perfect match.
    /// Weights are chosen so title mismatch dominates, author mismatch is a
    /// strong secondary signal, and edition_count is a tiebreaker only.
    /// </summary>
    private static int ScoreCandidate(JsonElement entry, string queryTitle, string? queryAuthor, int? queryYear)
    {
        int score = 0;

        // 1. Title similarity via token coverage (NOT Levenshtein). OpenLibrary's
        //    title index is notoriously messy — entries like "Dune: House Atreides"
        //    show up as "House Atreides (Dune" (no closing paren), "Dune House
        //    Atreides [Hardback]", etc. A character-level distance metric rejects
        //    those real matches because the punctuation differs, so we compare at
        //    the word level instead: how many of our query's meaningful tokens are
        //    missing from the result title?
        var docTitle = entry.TryGetProperty("title", out var tp) ? tp.GetString() : "";
        score += TitleMismatchScore(queryTitle, docTitle);

        // 2. Year proximity (only when we have a reference year). 1 year off = 5 points;
        //    10 years off = 50 points. Prefer the edition of the work closest to ours.
        if (queryYear.HasValue
            && entry.TryGetProperty("first_publish_year", out var yp)
            && yp.ValueKind == JsonValueKind.Number)
        {
            score += Math.Abs(queryYear.Value - yp.GetInt32()) * 5;
        }

        // 3. Author alignment. This is the big change vs the old scorer: when we
        //    know the author, a wrong author is a ~200-point penalty — enough to
        //    dominate ties even when both the title and year match exactly. For
        //    multi-author strings any one surname match counts.
        if (!string.IsNullOrWhiteSpace(queryAuthor))
        {
            var surnames = ExtractSurnames(queryAuthor);
            if (!HasAuthor(entry))
                score += 500;                               // authorless entry, we know one exists
            else if (surnames.Count > 0 && !AuthorListContainsAny(entry, surnames))
                score += 200;                               // wrong author
        }

        // 4. Cover penalty — still kept, but smaller now that the sibling filter
        //    drops most authorless/coverless stubs before we get here.
        if (!HasCover(entry))
            score += 50;

        // 5. Tiebreaker: edition_count. A work with many editions is almost
        //    always the canonical one. Caps at -10 so it can't override real
        //    title/author mismatches, but will reliably break exact-match ties.
        if (entry.TryGetProperty("edition_count", out var ec) && ec.ValueKind == JsonValueKind.Number)
        {
            var editions = ec.GetInt32();
            score -= Math.Min(editions, 100) / 10;
        }

        return score;
    }

    private static bool HasCover(JsonElement entry) =>
        entry.TryGetProperty("cover_i", out var ci) && ci.ValueKind == JsonValueKind.Number;

    private static bool HasAuthor(JsonElement entry) =>
        entry.TryGetProperty("author_name", out var ap)
        && ap.ValueKind == JsonValueKind.Array
        && ap.GetArrayLength() > 0;

    /// <summary>
    /// True when any name in the entry's <c>author_name</c> array contains at
    /// least one of the supplied surnames. Substring match (case-insensitive)
    /// so "Herbert" matches "Brian Herbert" and "Herbert, Brian" alike.
    /// </summary>
    private static bool AuthorListContainsAny(JsonElement entry, HashSet<string> surnames)
    {
        if (surnames.Count == 0) return false;
        if (!entry.TryGetProperty("author_name", out var authors)
            || authors.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var a in authors.EnumerateArray())
        {
            var name = a.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var surname in surnames)
            {
                if (name.IndexOf(surname, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Extract every surname from a (potentially multi-author) author string.
    /// Embedded EPUB metadata commonly presents co-authors as
    /// <c>"Brian Herbert and Kevin J. Anderson"</c> or <c>"King, Stephen &amp; O'Nan, Stewart"</c>.
    /// Splitting on the common separators and taking the last token of each
    /// piece yields <c>{"Herbert", "Anderson"}</c> — the filter then keeps OL
    /// entries whose <c>author_name</c> contains any of those surnames, rather
    /// than only the last one, which was silently excluding most of the
    /// canonical entries for multi-author works.
    /// </summary>
    private static HashSet<string> ExtractSurnames(string fullName)
    {
        var surnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fullName)) return surnames;

        // Split into individual author pieces. The separators are greedy — we
        // don't try to distinguish "and" inside a legitimate single-author name
        // (rare in practice) from the conjunction.
        var pieces = Regex.Split(fullName, @"\s+(?:and|&)\s+|,\s*", RegexOptions.IgnoreCase);
        foreach (var piece in pieces)
        {
            var trimmed = piece.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var lastSpace = trimmed.LastIndexOf(' ');
            var surname = lastSpace >= 0 ? trimmed.Substring(lastSpace + 1) : trimmed;
            if (surname.Length > 1) surnames.Add(surname);
        }
        return surnames;
    }

    /// <summary>
    /// Token-coverage title score — zero when every meaningful word in the query
    /// title appears somewhere in the candidate title (regardless of order or
    /// punctuation), with a proportional penalty per missing token. The
    /// coverage-ratio floor ensures a single-word query that misses its only
    /// token still scores at least the rejection threshold instead of the
    /// dangerously low raw 40.
    /// </summary>
    private static int TitleMismatchScore(string queryTitle, string? candidateTitle)
    {
        var qTokens = Tokenize(queryTitle);
        var cTokens = Tokenize(candidateTitle ?? string.Empty);
        if (qTokens.Count == 0 || cTokens.Count == 0) return 100;

        int missing = qTokens.Count(t => !cTokens.Contains(t));
        int absolute = missing * 40;
        int proportional = (missing * 100) / qTokens.Count;
        return Math.Max(absolute, proportional);
    }

    /// <summary>
    /// Lowercases, strips punctuation, and drops one-letter tokens so the
    /// comparison is word-based rather than character-based. One-letter tokens
    /// (the possessive "s" after apostrophe, stray initials) add noise without
    /// discriminating power.
    /// </summary>
    private static HashSet<string> Tokenize(string input)
    {
        var normalized = Regex.Replace(input.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .Where(w => w.Length > 1);
        return new HashSet<string>(tokens, StringComparer.Ordinal);
    }
}
