using SoftMedia.Server.Constants;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

public record MediaExtra(int Index, string Title, string Kind, string FileName, long SizeBytes);

/// <summary>
/// NR-WI-014 — surfaces companion clips (trailers, samples, featurettes) for a title.
/// Deliberately DB-free: extras are probed from the filesystem at request time, so
/// they can never pollute browse/search/home/hero (there are no rows to leak) and
/// need no scanner or schema changes. The probe is deterministic (sorted) so an index
/// handed to the client resolves to the same file on the follow-up stream request.
/// </summary>
public interface IExtrasService
{
    /// <summary>Extras for a Movie (file path) or Series (folder path) item. Empty for other types.</summary>
    List<MediaExtra> GetExtras(MediaItem item);

    /// <summary>Re-probes and resolves the extra at <paramref name="index"/> to its absolute path. Null when out of range.</summary>
    string? ResolveExtraPath(MediaItem item, int index);
}

public class ExtrasService : IExtrasService
{
    private readonly ILogger<ExtrasService> _logger;

    public ExtrasService(ILogger<ExtrasService> logger)
    {
        _logger = logger;
    }

    public List<MediaExtra> GetExtras(MediaItem item)
    {
        var files = ProbeFiles(item);
        return files
            .Select((f, i) => new MediaExtra(i, CleanTitle(item, f), KindOf(f), Path.GetFileName(f), SafeLength(f)))
            .ToList();
    }

    public string? ResolveExtraPath(MediaItem item, int index)
    {
        var files = ProbeFiles(item);
        return index >= 0 && index < files.Count ? files[index] : null;
    }

    /// <summary>
    /// Sorted absolute paths of this item's companion videos: same-folder files carrying
    /// a companion suffix on the item's stem, plus everything in well-known extras
    /// subfolders. Series items probe their folder; movies probe the file's folder.
    /// </summary>
    private List<string> ProbeFiles(MediaItem item)
    {
        try
        {
            string? folder = item.Type switch
            {
                MediaType.Movie => Path.GetDirectoryName(item.Path),
                MediaType.Series => item.Path,
                _ => null,
            };
            if (folder is null || !Directory.Exists(folder)) return new List<string>();

            var results = new List<string>();
            var movieStem = item.Type == MediaType.Movie
                ? Path.GetFileNameWithoutExtension(item.Path)
                : null;

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!IsVideo(file) || !MediaCompanions.HasCompanionSuffix(file)) continue;
                // For movies, only companions of THIS title — a shared folder can hold
                // several movies, each with its own trailer. Boundary-prefix matching in
                // either direction, so "Film-trailer" pairs with the release-tagged
                // "Film.1080p.BDRip.mkv" (users name trailers without the tags).
                if (movieStem != null && !StemMatchesTitle(CompanionBase(file), movieStem))
                    continue;
                results.Add(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (!MediaCompanions.Folders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
                // Reparse points are skipped for the same reason the scanners skip them:
                // a symlinked "extras" folder must not pull outside content into range.
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                results.AddRange(Directory.EnumerateFiles(sub).Where(IsVideo));
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extras probe failed for {Path}", item.Path);
            return new List<string>();
        }
    }

    private static bool IsVideo(string file) =>
        MediaExtensions.Video.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
        && !Helpers.MediaPathSafety.HasArgumentInjectionRisk(file);

    /// <summary>The companion's stem with its "-trailer"/"-sample"/… suffix removed.</summary>
    private static string CompanionBase(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var suffix in MediaCompanions.Suffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return stem[..^suffix.Length];
        }
        return stem;
    }

    /// <summary>
    /// True when one name is a prefix of the other ON A SEPARATOR BOUNDARY. Handles the
    /// dominant real-world case — trailers named without the movie file's release tags
    /// ("Film-trailer.mkv" beside "Film.1080p.BDRip.x264.mkv") — while the boundary
    /// requirement keeps "Aliens-trailer" from ever attaching to "Alien".
    /// </summary>
    private static bool StemMatchesTitle(string companionBase, string movieStem)
    {
        static bool IsSeparator(char c) => c is '.' or '-' or '_' or ' ' or '(' or '[';
        static bool PrefixOnBoundary(string longer, string shorter) =>
            longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
            && (longer.Length == shorter.Length || IsSeparator(longer[shorter.Length]));

        return PrefixOnBoundary(companionBase, movieStem) || PrefixOnBoundary(movieStem, companionBase);
    }

    private static string KindOf(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var suffix in MediaCompanions.Suffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return suffix.TrimStart('-');
        }
        var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        return MediaCompanions.Folders.Contains(parent, StringComparer.OrdinalIgnoreCase)
            ? parent.ToLowerInvariant().TrimEnd('s')
            : "extra";
    }

    private static string CleanTitle(MediaItem item, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var itemStem = item.Type == MediaType.Movie ? Path.GetFileNameWithoutExtension(item.Path) : item.Title;

        // "Movie (2020)-trailer" or "Movie-trailer" beside "Movie.1080p.mkv" -> "Trailer";
        // "extras/Making Of.mkv" -> "Making Of".
        if (MediaCompanions.HasCompanionSuffix(file) && StemMatchesTitle(CompanionBase(file), itemStem))
        {
            stem = KindOf(file);
        }
        else if (stem.StartsWith(itemStem, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[itemStem.Length..].TrimStart('-', '.', ' ', '_');
            if (string.IsNullOrWhiteSpace(stem)) stem = KindOf(file);
        }

        return char.ToUpperInvariant(stem[0]) + stem[1..];
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }
}
