using System.Text.RegularExpressions;

namespace SoftMedia.Server.Helpers;

public static class FileNameParser
{
    private static readonly Regex[] MoviePatterns = new[]
    {
        new Regex(@"(?i)^(?<Title>.*?)[ ._-]+(?<Year>\d{4})(?=[ ._-]|$)", RegexOptions.Compiled), // Title Year
        new Regex(@"(?i)^(?<Title>.*?)[ ._-]*\(\b(?<Year>\d{4})\b\)", RegexOptions.Compiled), // Title (Year)
        new Regex(@"(?i)^(?<Year>\d{4})[ ._-]+(?<Title>.*?)$", RegexOptions.Compiled), // Year Title
        new Regex(@"(?i)^(?<Title>.*?)[ ._-]+(?<Quality>1080p|720p|2160p|4k|bluray|web-dl|webrip)", RegexOptions.Compiled) // Title Quality
    };

    private static readonly Regex[] TvPatterns = new[]
    {
        new Regex(@"(?i)^(?:(.*?)[ ._-]+)?S(\d{1,2})E(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled), // Show S01E01 Title
        new Regex(@"(?i)^(?:(.*?)[ ._-]+)?(\d{1,2})x(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled),   // Show 1x01 Title
        new Regex(@"(?i)^(?:(.*?)[ ._-]+)?Season[ ._-]*(\d{1,2})[ ._-]+Episode[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled), // Show Season 1 Episode 1 Title
        // Mini-series patterns (no season, episode only)
        new Regex(@"(?i)^Episode[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled), // Episode 1 or Episode 01
        new Regex(@"(?i)^E(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled), // E1 or E01
        new Regex(@"(?i)^Part[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled), // Part 1 or Part 01
        new Regex(@"^(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled) // Just "01" or "1" at start
    };
    
    // Patterns that return just episode number (Season defaults to 1)
    private static readonly Regex[] MiniSeriesPatterns = new[]
    {
        new Regex(@"(?i)^Episode[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled),
        new Regex(@"(?i)^E(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled),
        new Regex(@"(?i)^Part[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled),
        new Regex(@"^(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled)
    };

    public static (string Title, int? Year) ParseMovie(string fileName)
    {
        var cleanName = Path.GetFileNameWithoutExtension(fileName);
        int? year = null;
        string titlePart = cleanName;

        foreach (var pattern in MoviePatterns)
        {
            var match = pattern.Match(cleanName);
            if (match.Success)
            {
                titlePart = match.Groups["Title"].Value;
                if (match.Groups["Year"].Success && int.TryParse(match.Groups["Year"].Value, out var y) && y > 1900 && y < 2100)
                {
                    year = y;
                }
                break;
            }
        }

        return (CleanName(titlePart), year);
    }

    public static (string ShowName, int Season, int Episode, string EpisodeTitle) ParseTvEpisode(string fileName)
    {
        var cleanName = Path.GetFileNameWithoutExtension(fileName);
        
        // First try standard patterns (with show name and season)
        for (int i = 0; i < 3; i++) // First 3 patterns have 4 groups: show, season, episode, title
        {
            var match = TvPatterns[i].Match(cleanName);
            if (match.Success)
            {
                var showName = CleanName(match.Groups[1].Value);
                var season = int.Parse(match.Groups[2].Value);
                var episode = int.Parse(match.Groups[3].Value);
                var rawTitle = match.Groups.Count > 4 && match.Groups[4].Success ? match.Groups[4].Value : string.Empty;
                var episodeTitle = CleanName(rawTitle);
                
                return (showName, season, episode, episodeTitle);
            }
        }
        
        // Try mini-series patterns (episode only, season defaults to 1, show name from directory)
        foreach (var pattern in MiniSeriesPatterns)
        {
            var match = pattern.Match(cleanName);
            if (match.Success)
            {
                var episode = int.Parse(match.Groups[1].Value);
                var rawTitle = match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                var episodeTitle = CleanName(rawTitle);
                
                // Show name will come from directory, return empty
                return (string.Empty, 1, episode, episodeTitle);
            }
        }

        return (string.Empty, 0, 0, string.Empty);
    }

    public static (string Title, int? TrackNumber) ParseMusic(string fileName)
    {
        var cleanName = Path.GetFileNameWithoutExtension(fileName);
        
        // Regex for "01 Title" or "01 - Title" or "01. Title"
        var match = Regex.Match(cleanName, @"^(\d+)(?:[\s\.\-_]+)(.+)$");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var track))
            {
                return (match.Groups[2].Value.Trim(), track);
            }
        }
        
        return (cleanName, null);
    }

    /// <summary>
    /// Parses a book filename into (Author, Title, Year).
    /// <para>
    /// Real-world book libraries use wildly inconsistent naming, so this is a
    /// best-effort heuristic chain rather than a single regex. Every rule is
    /// tried in order; the first one that produces a plausible split wins.
    /// Fall back to embedded file metadata (EPUB OPF / PDF Info dict) when
    /// the filename is unambiguous junk.
    /// </para>
    /// Supported patterns (non-exhaustive):
    ///   "Author - Title"                                 classic convention
    ///   "Author - Title (YYYY)"                          plus publication year
    ///   "1 - Title - Author (YYYY)"                      series ordinal prefix
    ///   "Series 1 - Title"                               series + ordinal inline
    ///   "Title by Author"                                English-language style
    ///   "Lastname, Firstname - Title"                    library-catalog style
    ///   "(Tag) Author - Title"                           bracketed pre-tags
    ///   "Title"                                          degenerate — whole filename
    /// </summary>
    public static (string Author, string Title, int? Year) ParseBook(string fileName)
    {
        var cleanName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(cleanName))
            return (string.Empty, string.Empty, null);

        // 1. Capture + strip trailing "(YYYY)".
        int? year = null;
        var yearMatch = Regex.Match(cleanName, @"\s*\((?<y>(19|20)\d{2})\)\s*$");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups["y"].Value, out var y) && y >= 1900 && y <= 2100)
        {
            year = y;
            cleanName = cleanName.Substring(0, yearMatch.Index).TrimEnd();
        }

        // 2. Strip leading bracketed tags, e.g. "(Ebook - Comic) Manara, Milo - ...".
        //    Loop because some rippers stack multiple brackets.
        while (true)
        {
            var bracketMatch = Regex.Match(cleanName, @"^\s*[\(\[][^\)\]]+[\)\]]\s*");
            if (!bracketMatch.Success) break;
            cleanName = cleanName.Substring(bracketMatch.Length).TrimStart();
            if (string.IsNullOrEmpty(cleanName)) return (string.Empty, string.Empty, year);
        }

        // 3. Strip a leading series-ordinal prefix like "1 - ", "2.5 - ", "Book 3 - ".
        //    The ordinal signals position-in-series, not author, and throws off the
        //    naive split below. Constraints to avoid false positives:
        //      - At most 3 digits — rejects 4-digit year prefixes ("1922 - ...").
        //      - Requires a space on BOTH sides of the dash — rejects date-style
        //        prefixes with hyphens ("11-22-63 - A Novel"), where the leading
        //        number is part of the title.
        var ordinalMatch = Regex.Match(cleanName, @"^(?:Book\s+)?\d{1,3}(?:\.\d+)?\s+-\s+", RegexOptions.IgnoreCase);
        if (ordinalMatch.Success)
        {
            cleanName = cleanName.Substring(ordinalMatch.Length).TrimStart();
        }

        // 4. "Title by Author" — English prose convention; the "by" is unambiguous.
        var byMatch = Regex.Match(cleanName, @"^(?<title>.+?)\s+by\s+(?<author>[^-]+?)\s*$",
            RegexOptions.IgnoreCase);
        if (byMatch.Success)
        {
            var t = CleanName(byMatch.Groups["title"].Value.Trim());
            var a = NormalizeAuthor(byMatch.Groups["author"].Value.Trim());
            if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(a))
                return (a, t, year);
        }

        // 5. "Lastname, Firstname - Title" — library-catalog convention.
        //    The comma inside the author segment prevents the naive split from
        //    working, so we detect it up front.
        var commaMatch = Regex.Match(cleanName,
            @"^(?<last>[A-Z][A-Za-zÀ-ÿ'\-]+)\s*,\s*(?<first>[A-Z][A-Za-zÀ-ÿ'\-\.\s]+?)\s*-\s*(?<title>.+)$");
        if (commaMatch.Success)
        {
            var a = $"{commaMatch.Groups["first"].Value.Trim()} {commaMatch.Groups["last"].Value.Trim()}";
            var t = CleanName(commaMatch.Groups["title"].Value.Trim());
            return (a, t, year);
        }

        // 6. " - " split. Filename may have multiple " - " separators, so we
        //    look at both ends and keep whichever end looks like an author name.
        var parts = cleanName.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .Where(p => p.Length > 0)
                             .ToList();

        if (parts.Count >= 2)
        {
            // 6a. Strip a leading "Series Name N" prefix when the first segment
            //     ends in a digit (e.g. "Heroes of Dune 1 - Paul of Dune"). The
            //     series+ordinal is context, not author. Only apply when this
            //     leaves us a real remainder — otherwise we lose the whole name.
            if (parts.Count >= 2 && Regex.IsMatch(parts[0], @"\s\d+(?:\.\d+)?$"))
            {
                parts.RemoveAt(0);
                if (parts.Count == 1)
                    return (string.Empty, CleanName(parts[0]), year);
            }

            bool firstIsName = LooksLikePersonalName(parts[0]);
            bool lastIsName = LooksLikePersonalName(parts[^1]);

            // 6b. Both ends look like names — e.g. "Dune - Frank Herbert" (single-
            //     word title), "Different Seasons - Stephen King" (title that
            //     reads as a name), "Dolores Claiborne_ A Novel - Stephen King"
            //     (padded subtitle). Resolution priority:
            //       1. Strong path hint — one segment's tokens appear ≥2 times
            //          in the parent directory and the other's 0 times. Almost
            //          always indicates "this folder is the author's collection"
            //          and trumps any word-count reading.
            //       2. Word count — fuller name is more likely the author.
            //       3. Weak path hint — any non-zero overlap difference.
            //       4. Default to first-wins (classic Author-Title).
            if (firstIsName && lastIsName)
            {
                int firstWords = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                int lastWords = parts[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

                var parentTokens = ExtractParentDirTokens(fileName);
                int firstOverlap = parentTokens.Count > 0 ? CountTokenOverlap(parts[0], parentTokens) : 0;
                int lastOverlap = parentTokens.Count > 0 ? CountTokenOverlap(parts[^1], parentTokens) : 0;

                // Strong path hint — one side's full name is in the parent dir.
                if (lastOverlap >= 2 && firstOverlap == 0)
                {
                    return (NormalizeAuthor(parts[^1]),
                            CleanName(string.Join(" - ", parts.Take(parts.Count - 1))),
                            year);
                }
                if (firstOverlap >= 2 && lastOverlap == 0)
                {
                    return (NormalizeAuthor(parts[0]),
                            CleanName(string.Join(" - ", parts.Skip(1))),
                            year);
                }

                // Word-count preference — fuller name wins as author.
                if (lastWords > firstWords)
                {
                    return (NormalizeAuthor(parts[^1]),
                            CleanName(string.Join(" - ", parts.Take(parts.Count - 1))),
                            year);
                }
                if (firstWords > lastWords)
                {
                    return (NormalizeAuthor(parts[0]),
                            CleanName(string.Join(" - ", parts.Skip(1))),
                            year);
                }

                // Weak path hint — any non-zero delta.
                if (lastOverlap > firstOverlap)
                {
                    return (NormalizeAuthor(parts[^1]),
                            CleanName(string.Join(" - ", parts.Take(parts.Count - 1))),
                            year);
                }
                if (firstOverlap > lastOverlap)
                {
                    return (NormalizeAuthor(parts[0]),
                            CleanName(string.Join(" - ", parts.Skip(1))),
                            year);
                }

                // Fully ambiguous — default to classic Author-Title.
                return (NormalizeAuthor(parts[0]),
                        CleanName(string.Join(" - ", parts.Skip(1))),
                        year);
            }

            // 6c. Classic "Author - Title" — only the first segment is a name.
            if (firstIsName)
            {
                return (NormalizeAuthor(parts[0]),
                        CleanName(string.Join(" - ", parts.Skip(1))),
                        year);
            }

            // 6d. "Title - Author" — only the last segment is a name.
            //     Everything before it is the title (joined back so a title that
            //     itself contained " - " is preserved).
            if (lastIsName)
            {
                return (NormalizeAuthor(parts[^1]),
                        CleanName(string.Join(" - ", parts.Take(parts.Count - 1))),
                        year);
            }

            // 6e. Neither end looks like a name — no author in the filename.
            return (string.Empty, CleanName(string.Join(" - ", parts)), year);
        }

        // 7. Degenerate: whole filename is the title. OpenLibraryProvider has a
        //    separate guard against "title that's really just an author name".
        return (string.Empty, CleanName(cleanName), year);
    }

    /// <summary>
    /// A heuristic "does this string look like a person's name?" check. We use it
    /// to decide which end of a " - " split is the author vs the title. The rule
    /// is deliberately strict — a false positive hands the user a book with the
    /// title as author, which is more confusing than leaving author blank.
    /// Also recognises co-author patterns ("Joe Hill and Stephen King", "X &amp; Y")
    /// by validating each piece is itself a 2+ capitalised-word name — a stricter
    /// test than blindly allowing "and" as a connector (that caused "Pride and
    /// Prejudice" to read as a name).
    /// </summary>
    private static bool LooksLikePersonalName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        // Reject obvious non-names first — anything with digits or excessive
        // punctuation is almost certainly a title.
        if (Regex.IsMatch(s, @"\d")) return false;
        if (s.Contains('(') || s.Contains(')') || s.Contains(':')) return false;

        // Co-author pattern detection: if the string contains " and " / " & ",
        // split and require EVERY piece to be a 2+ capitalised-word name. The
        // 2+ word requirement is critical — "Pride and Prejudice" splits into
        // ["Pride", "Prejudice"], both single-word, and must be rejected as a
        // name. "Joe Hill and Stephen King" has two 2-word pieces and passes.
        if (Regex.IsMatch(s, @"\s+(?:and|&)\s+", RegexOptions.IgnoreCase))
        {
            var pieces = Regex.Split(s, @"\s+(?:and|&)\s+", RegexOptions.IgnoreCase);
            foreach (var piece in pieces)
            {
                var pieceTrimmed = piece.Trim();
                var pieceWordCount = pieceTrimmed
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (pieceWordCount < 2) return false;
                if (!IsFullName(pieceTrimmed)) return false;
            }
            return true;
        }

        return IsFullName(s);
    }

    /// <summary>
    /// A single-author-string check — used both directly and as the per-piece
    /// predicate of the co-author split above. Requires 2+ capitalised words,
    /// no lowercase connectors, no leading article.
    /// </summary>
    private static bool IsFullName(string s)
    {
        var words = s.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1 || words.Length > 5) return false;

        // Titles frequently start with an article ("The Sandworms", "A Tale");
        // names never do.
        if (words[0].Equals("The", StringComparison.OrdinalIgnoreCase)
         || words[0].Equals("A", StringComparison.OrdinalIgnoreCase)
         || words[0].Equals("An", StringComparison.OrdinalIgnoreCase))
            return false;

        // Every word must start uppercase. A lowercase connector like "of" or
        // "and" reads as a title ("Pride and Prejudice", "Paul of Dune").
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            if (!char.IsUpper(w[0])) return false;
        }
        return true;
    }

    /// <summary>
    /// Tokenize the parent directory name (not the full path) into lowercase
    /// significant words. Used only as a tie-break hint when both ends of the
    /// filename could plausibly be an author name — rippers commonly organise
    /// by author ("Stephen King 121 Books Epub Collection"), so any segment
    /// that shares tokens with the parent folder is the more likely author.
    /// </summary>
    private static HashSet<string> ExtractParentDirTokens(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return new HashSet<string>();
            var parentName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(parentName)) return new HashSet<string>();
            var normalized = Regex.Replace(parentName.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
            return new HashSet<string>(
                normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                          .Where(w => w.Length > 1),
                StringComparer.Ordinal);
        }
        catch
        {
            // Path may be malformed — a hint's absence is not a parser failure.
            return new HashSet<string>();
        }
    }

    private static int CountTokenOverlap(string segment, HashSet<string> parentTokens)
    {
        var normalized = Regex.Replace(segment.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        return normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                         .Where(w => w.Length > 1)
                         .Count(parentTokens.Contains);
    }

    /// <summary>
    /// Cleans up an author string: collapses whitespace, strips ampersand noise,
    /// and normalizes "Lastname, Firstname" → "Firstname Lastname".
    /// </summary>
    private static string NormalizeAuthor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var author = raw.Replace("_", " ").Trim();
        author = Regex.Replace(author, @"\s+", " ");

        // "King, Stephen" → "Stephen Lastname"
        var parts = author.Split(',', 2);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            author = $"{parts[1].Trim()} {parts[0].Trim()}";
        }

        return author;
    }

    /// <summary>
    /// Parses a comic filename into (SeriesName, IssueNumber, Year).
    /// Supports common conventions:
    ///   "Series Name 001"
    ///   "Series Name #001"
    ///   "Series Name Issue 001"
    ///   "Series Name Vol 1 #001"
    ///   "Series Name 001 (2023)"
    ///   "Series Name - 001"
    /// Issue number and year are optional; if issue isn't detected the whole
    /// cleaned name becomes the series (one-shot comic).
    /// </summary>
    public static (string SeriesName, int? IssueNumber, int? Year) ParseComic(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
            return (string.Empty, null, null);

        // Extract year from (YYYY) before stripping parens so we don't lose it.
        int? year = null;
        var yearMatch = Regex.Match(baseName, @"\((\d{4})\)");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var y) && y >= 1900 && y <= 2100)
        {
            year = y;
        }

        // Try patterns in order of specificity. Each returns (seriesName, issueNumber).
        // We anchor issue tokens so "Star Wars 1977" (a year-like trailing number)
        // isn't misread as issue 1977 — we require an explicit marker (#, Issue, or
        // an issue that's at the end AND not inside parens).
        var patterns = new[]
        {
            // "Series #NNN" or "Series #NNN.N"
            @"^(?<series>.+?)\s*#\s*(?<issue>\d{1,4})(?:\.\d+)?\s*(?:\(\d{4}\))?\s*$",
            // "Series Issue NNN"
            @"^(?<series>.+?)\s+(?:Issue|Iss\.?|No\.?|Number)\s+(?<issue>\d{1,4})\s*(?:\(\d{4}\))?\s*$",
            // "Series - NNN"
            @"^(?<series>.+?)\s-\s*(?<issue>\d{1,4})\s*(?:\(\d{4}\))?\s*$",
            // "Series NNN (YYYY)" — requires parenthesized year to disambiguate
            @"^(?<series>.+?)\s+(?<issue>\d{1,4})\s*\(\d{4}\)\s*$",
            // Vol suffix: "Series v1 #NNN" / "Series Vol 2 #NNN"
            @"^(?<series>.+?)\s+(?:v|Vol\.?)\s*\d{1,2}\s*#?\s*(?<issue>\d{1,4})\s*(?:\(\d{4}\))?\s*$",
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(baseName, pattern, RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups["issue"].Value, out var iss))
            {
                var rawSeries = m.Groups["series"].Value.Trim();
                // Strip a trailing (YYYY) from the series — it belongs to the year.
                rawSeries = Regex.Replace(rawSeries, @"\s*\(\d{4}\)\s*$", "").Trim();
                // Strip a trailing volume marker ("v1", "Vol 2", "Volume 3").
                rawSeries = Regex.Replace(rawSeries, @"\s+(?:v|Vol\.?|Volume)\s*\d{1,2}\s*$", "",
                    RegexOptions.IgnoreCase).Trim();
                return (CleanName(rawSeries), iss, year);
            }
        }

        // Fallback: no issue number — one-shot. Strip year parens, clean rest.
        var fallback = Regex.Replace(baseName, @"\s*\(\d{4}\)\s*$", "").Trim();
        return (CleanName(fallback), null, year);
    }

    public static (string Title, int? Year) ParseGame(string filePath)
    {
        var cleanName = Path.GetFileNameWithoutExtension(filePath);
        
        // If it's a generic executable name, use the parent directory name instead
        var genericNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "game", "setup", "install", "run", "play", "start", "launcher", "autorun"
        };
        
        if (genericNames.Contains(cleanName))
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (!string.IsNullOrEmpty(dirName))
            {
                cleanName = dirName;
            }
        }
        
        // Extract year before stripping parens, so that e.g. Game Title (1998) is caught
        var year = ExtractYear(cleanName);
        
        // Strip ROM/Region tags in brackets/parens (e.g. [!], (USA), (Rev 1), [T+Eng])
        cleanName = Regex.Replace(cleanName, @"\[[^\]]+\]", "");
        cleanName = Regex.Replace(cleanName, @"\([^\)]+\)", "");
        
        return (CleanName(cleanName), year);
    }

    private static string CleanName(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        
        // Initial cleanup
        var cleaned = title.Replace(".", " ").Replace("_", " ").Replace("-", " ").Replace("[", " ").Replace("]", " ").Replace("(", " ").Replace(")", " ").Trim();
        
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resultWords = new System.Collections.Generic.List<string>();
        
        // Enhanced junk patterns
        var junkPatterns = new Regex(@"^(1080p|720p|2160p|480p|4k|web|web-dl|webrip|bluray|bdrip|dvdrip|h264|h265|x264|x265|hevc|aac|ac3|ddp\d*|dsnp|eztv|flux|lazycunts|hdtv|proper|repack|truehd|dts|dts-hd|atmos|\d{3,4}x\d{3,4})$", RegexOptions.IgnoreCase);

        foreach (var word in words)
        {
            if (junkPatterns.IsMatch(word)) break;
            resultWords.Add(word);
        }
        
        return string.Join(" ", resultWords);
    }

    private static string CleanTitle(string title) => CleanName(title); // Backwards compatibility if needed, or just remove
    private static string CleanEpisodeTitle(string title) => CleanName(title); // Backwards compatibility

    /// <summary>
    /// Extracts a year (1900-2099) from a folder or show name.
    /// Example: "The Hitchhikers Guide To The Galaxy - Remastered Mini Series 1981 1080p" -> 1981
    /// </summary>
    public static int? ExtractYear(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        
        // Find all 4-digit numbers that look like years
        var matches = Regex.Matches(text, @"\b(19|20)\d{2}\b");
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Value, out var year) && year >= 1900 && year <= 2099)
            {
                return year;
            }
        }
        return null;
    }

    /// <summary>
    /// Cleans a show name by removing release info (Remastered, Mini Series, etc.),
    /// years, quality tags, and other extraneous information.
    /// Example: "The Hitchhikers Guide To The Galaxy - Remastered Mini Series 1981 1080p" 
    ///       -> "The Hitchhikers Guide To The Galaxy"
    /// </summary>
    public static string CleanShowName(string showName)
    {
        if (string.IsNullOrEmpty(showName)) return string.Empty;

        // First, apply basic cleaning (dots, underscores, brackets to spaces)
        var cleaned = showName.Replace(".", " ").Replace("_", " ").Replace("[", " ").Replace("]", " ").Replace("(", " ").Replace(")", " ").Trim();
        
        // Split on common delimiters that often separate the title from release info
        // e.g., "Show Name - Remastered 1981 1080p" -> take "Show Name"
        var parts = Regex.Split(cleaned, @"\s+-\s+");
        
        // For each part, check if it looks like release info vs actual title
        var titleCandidates = new List<string>();
        foreach (var part in parts)
        {
            var trimmedPart = part.Trim();
            // Check if this part is primarily release info (contains year + quality, or release keywords)
            var hasYear = Regex.IsMatch(trimmedPart, @"\b(19|20)\d{2}\b");
            var hasQuality = Regex.IsMatch(trimmedPart, @"\b(1080p|720p|2160p|480p|4k|hdr|bluray|web-dl|webrip|dvdrip|bdrip)\b", RegexOptions.IgnoreCase);
            var hasReleaseKeywords = Regex.IsMatch(trimmedPart, @"\b(remastered|remux|extended|theatrical|unrated|directors?\s*cut|mini\s*series|complete|collection)\b", RegexOptions.IgnoreCase);
            
            // If this part has quality indicators or release keywords along with a year, it's likely release info
            if ((hasYear && hasQuality) || (hasYear && hasReleaseKeywords) || (hasQuality && hasReleaseKeywords))
            {
                // This looks like release info, skip it
                continue;
            }
            
            titleCandidates.Add(trimmedPart);
        }
        
        // Use the first valid part as the title
        if (titleCandidates.Count > 0)
        {
            cleaned = titleCandidates[0];
        }
        
        // Now apply word-level cleaning to strip trailing junk
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resultWords = new List<string>();
        
        // Patterns to stop at (quality, codec, year followed by nothing or more junk)
        var stopPatterns = new Regex(@"^(1080p|720p|2160p|480p|4k|web|web-dl|webrip|bluray|bdrip|dvdrip|h264|h265|x264|x265|hevc|hdtv|hdr|remux)$", RegexOptions.IgnoreCase);
        var yearPattern = new Regex(@"^(19|20)\d{2}$");
        
        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i];
            
            // Stop at quality/codec indicators
            if (stopPatterns.IsMatch(word)) break;
            
            // Stop at a year if followed by quality/junk or at end
            if (yearPattern.IsMatch(word))
            {
                // Check if this is likely a release year (at end or followed by junk)
                bool isReleaseYear = (i == words.Length - 1) || 
                                     (i < words.Length - 1 && stopPatterns.IsMatch(words[i + 1]));
                if (isReleaseYear) break;
            }
            
            resultWords.Add(word);
        }
        
        return string.Join(" ", resultWords).Trim();
    }
}
