namespace SoftMedia.Server.Services.Media;

/// <summary>One cache area's on-disk footprint.</summary>
public record CacheAreaStats(string Area, int Files, long Bytes);

/// <summary>
/// MC-WI-007 — per-area size/count report for everything under wwwroot/cache, so cache
/// growth is visible on the admin page instead of only discoverable from a shell.
/// (A 4.1 GB orphaned-trickplay pile sat unnoticed for weeks before 2026-07-29 exactly
/// because nothing surfaced these numbers.)
/// </summary>
public interface ICacheStatsService
{
    Task<IReadOnlyList<CacheAreaStats>> GetStatsAsync(CancellationToken ct = default);
}

public class CacheStatsService : ICacheStatsService
{
    private readonly string _cacheRoot;
    private readonly ILogger<CacheStatsService> _logger;

    public CacheStatsService(IWebHostEnvironment env, ILogger<CacheStatsService> logger)
    {
        var webRoot = !string.IsNullOrEmpty(env.WebRootPath)
            ? env.WebRootPath
            : Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        _cacheRoot = Path.Combine(webRoot, "cache");
        _logger = logger;
    }

    // (area label, path under cache/, recurse). "Artwork (TV)" is top-only because
    // tv/cast is reported as its own row; trickplay is one directory per item.
    private static readonly (string Area, string[] Segments, bool Recurse)[] Areas =
    {
        ("Artwork — movies", new[] { "images", "movies" }, false),
        ("Artwork — TV", new[] { "images", "tv" }, false),
        ("Artwork — cast", new[] { "images", "tv", "cast" }, false),
        ("Artwork — music", new[] { "images", "music" }, false),
        ("Artwork — games", new[] { "images", "games" }, false),
        ("Artwork — books", new[] { "images", "books" }, false),
        ("Artwork — playlists", new[] { "images", "playlists" }, true),
        ("Thumbnails", new[] { "images", "thumbnails" }, false),
        ("Image proxy", new[] { "images", "proxy" }, false),
        ("Trickplay", new[] { "trickplay" }, true),
        ("Subtitles", new[] { "subtitles" }, false),
    };

    public Task<IReadOnlyList<CacheAreaStats>> GetStatsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<CacheAreaStats>>(() =>
        {
            var results = new List<CacheAreaStats>(Areas.Length);
            foreach (var (area, segments, recurse) in Areas)
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.Combine(new[] { _cacheRoot }.Concat(segments).ToArray());
                var files = 0;
                long bytes = 0;
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        foreach (var file in Directory.EnumerateFiles(dir, "*", option))
                        {
                            files++;
                            try { bytes += new FileInfo(file).Length; } catch { /* deleted mid-walk */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to size cache area {Area} at {Dir}", area, dir);
                }
                results.Add(new CacheAreaStats(area, files, bytes));
            }
            return results;
        }, ct);
}
