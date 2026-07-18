using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>Outcome of a sidecar sweep for one item.</summary>
/// <param name="Changed">Any DB field changed (caller must save + mark the item updated).</param>
/// <param name="LocalPosterRemoved">A previously-applied SIDECAR poster's file is gone —
/// the caller should force a metadata re-enqueue so provider art can return.</param>
public readonly record struct LocalArtworkResult(bool Changed, bool LocalPosterRemoved);

public interface ILocalArtworkService
{
    /// <summary>
    /// R-WI-014 — discover conventional local artwork sidecars in <paramref name="mediaFolder"/>
    /// (poster/folder/&lt;stem&gt;-poster and fanart/backdrop/&lt;stem&gt;-fanart with common image
    /// extensions), ingest them into the image cache under source-distinct keys
    /// ("…_poster_local"), and apply them with the local-art flags set. Local art WINS over
    /// provider art (Kodi/Plex convention) but must not stop the item's one-time metadata
    /// enrichment (see MetadataEnrichmentPolicy). Respects MetadataLocked. Never serves from
    /// the media folder — always cache copies, jailed to the folder.
    /// </summary>
    Task<LocalArtworkResult> ApplyLocalArtworkAsync(MediaItem item, string mediaFolder, string? fileStem);
}

public class LocalArtworkService : ILocalArtworkService
{
    /// <summary>Suffix marking cache files this service owns. Distinct from provider keys
    /// ("…_poster") and NFO-ingested keys ("…_poster_nfo") — the removal logic below must
    /// only ever clear art it applied itself (review HIGH: clearing NFO-sourced art caused a
    /// permanent clear→re-enrich→re-ingest cycle).</summary>
    public const string SidecarKeySuffix = "_local";

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    private readonly IImageCacheService _imageCache;
    private readonly ILogger<LocalArtworkService> _logger;

    public LocalArtworkService(IImageCacheService imageCache, ILogger<LocalArtworkService> logger)
    {
        _imageCache = imageCache;
        _logger = logger;
    }

    public async Task<LocalArtworkResult> ApplyLocalArtworkAsync(MediaItem item, string mediaFolder, string? fileStem)
    {
        try
        {
            return await ApplyCoreAsync(item, mediaFolder, fileStem);
        }
        catch (Exception ex)
        {
            // Artwork is an enhancement: an unreadable folder or IO hiccup must never fail
            // the scan of the media file itself.
            _logger.LogWarning(ex, "Local artwork sweep failed for {Title} in {Folder}", item.Title, mediaFolder);
            return new LocalArtworkResult(false, false);
        }
    }

    private async Task<LocalArtworkResult> ApplyCoreAsync(MediaItem item, string mediaFolder, string? fileStem)
    {
        // Locked items are admin-frozen: no artwork writes of any kind (same contract as
        // enrichment's single chokepoint).
        if (item.MetadataLocked) return new LocalArtworkResult(false, false);
        if (string.IsNullOrEmpty(mediaFolder) || !Directory.Exists(mediaFolder)) return new LocalArtworkResult(false, false);

        var (posterKey, backdropKey) = item.Type switch
        {
            MediaType.Movie => ($"movies/{item.Id}_poster{SidecarKeySuffix}", $"movies/{item.Id}_backdrop{SidecarKeySuffix}"),
            MediaType.Series => ($"tv/{item.Id}_poster{SidecarKeySuffix}", $"tv/{item.Id}_backdrop{SidecarKeySuffix}"),
            _ => (null as string, null as string),
        };
        if (posterKey == null) return new LocalArtworkResult(false, false); // only movie/TV in v1

        // One directory listing serves every candidate probe and the shared-folder guard.
        var allFiles = Directory.GetFiles(mediaFolder);
        var imagesByStem = allFiles
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToLookup(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);

        // Review MEDIUM: in a FLAT folder holding several movies, a bare poster.jpg belongs to
        // no one — applying it to every file overrides each movie's correct art. Folder-level
        // names only count when the folder is dedicated (≤1 video file). <stem>-poster is
        // per-file and always applies. Series sweeps pass fileStem=null and their folder is
        // dedicated by definition (the caller skips library roots).
        var folderIsDedicated = fileStem == null || CountVideoFiles(allFiles) <= 1;

        var posterSource = FindFirst(imagesByStem, Candidates(fileStem, "poster", folderIsDedicated, "poster", "folder"));
        var backdropSource = FindFirst(imagesByStem, Candidates(fileStem, "fanart", folderIsDedicated, "fanart", "backdrop"));

        var changed = false;
        var posterRemoved = false;

        if (posterSource != null)
        {
            var webPath = await _imageCache.CacheLocalImageAsync(posterSource, posterKey, mediaFolder);
            if (webPath != null && (item.PosterUrl != webPath || !item.PosterFromLocalFile))
            {
                item.PosterUrl = webPath;
                item.PosterFromLocalFile = true;
                changed = true;
                _logger.LogInformation("Applied local poster {File} to {Title}", Path.GetFileName(posterSource), item.Title);
            }
        }
        else if (OwnsCurrentArt(item.PosterUrl) && item.PosterFromLocalFile)
        {
            // The sidecar was deleted: clear the local art (and its cache copy — otherwise the
            // stale file lingers) so the enrichment gate sees a poster-less item and provider
            // art can come back on the forced re-enqueue. ONLY art this service applied is
            // cleared — NFO-ingested posters ("_poster_nfo") have their own lifecycle.
            _imageCache.DeleteCachedLocalImage(posterKey);
            item.PosterUrl = null;
            item.PosterFromLocalFile = false;
            changed = true;
            posterRemoved = true;
            _logger.LogInformation("Local poster removed for {Title}; provider art will be re-fetched.", item.Title);
        }

        if (backdropSource != null)
        {
            var webPath = await _imageCache.CacheLocalImageAsync(backdropSource, backdropKey!, mediaFolder);
            if (webPath != null && (item.BackdropUrl != webPath || !item.BackdropFromLocalFile))
            {
                item.BackdropUrl = webPath;
                item.BackdropFromLocalFile = true;
                changed = true;
            }
        }
        else if (OwnsCurrentArt(item.BackdropUrl) && item.BackdropFromLocalFile)
        {
            _imageCache.DeleteCachedLocalImage(backdropKey!);
            item.BackdropUrl = null;
            item.BackdropFromLocalFile = false;
            changed = true; // backdrops don't gate enrichment; the next refresh restores provider art
        }

        return new LocalArtworkResult(changed, posterRemoved);
    }

    /// <summary>True when the stored art URL points at a cache file THIS service created.</summary>
    private static bool OwnsCurrentArt(string? url)
        => url != null && url.Contains(SidecarKeySuffix + ".", StringComparison.OrdinalIgnoreCase);

    // Companion clips that don't make a folder "shared" (Radarr/Kodi conventions).
    private static readonly string[] CompanionSuffixes = { "-trailer", "-sample", "-extra" };

    private static int CountVideoFiles(string[] files)
        // MediaExtensions stores extensions WITHOUT the dot ("mkv"), Path.GetExtension yields ".mkv".
        // Trailers/samples beside the movie are companions of the SAME title, not other movies —
        // counting them broke the dedicated-folder detection for common Radarr layouts.
        => files.Count(f =>
            Constants.MediaExtensions.Video.Contains(Path.GetExtension(f).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
            && !CompanionSuffixes.Any(s => Path.GetFileNameWithoutExtension(f).EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Candidates(string? stem, string stemSuffix, bool includeBareNames, params string[] bareNames)
    {
        if (!string.IsNullOrEmpty(stem)) yield return $"{stem}-{stemSuffix}";
        if (!includeBareNames) yield break;
        foreach (var name in bareNames) yield return name;
    }

    private static string? FindFirst(ILookup<string, string> filesByStem, IEnumerable<string> candidateStems)
    {
        foreach (var stem in candidateStems)
        {
            var match = filesByStem[stem].OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }
}
