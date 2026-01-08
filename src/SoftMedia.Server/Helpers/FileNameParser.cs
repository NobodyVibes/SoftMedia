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
