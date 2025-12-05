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
        new Regex(@"(?i)^(?:(.*?)[ ._-]+)?Season[ ._-]*(\d{1,2})[ ._-]+Episode[ ._-]*(\d{1,2})(?:[ ._-]+(.*?))?$", RegexOptions.Compiled) // Show Season 1 Episode 1 Title
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
        
        foreach (var pattern in TvPatterns)
        {
            var match = pattern.Match(cleanName);
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

        return (string.Empty, 0, 0, string.Empty);
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
}
