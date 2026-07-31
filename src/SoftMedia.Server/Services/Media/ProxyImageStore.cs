using System.Security.Cryptography;
using System.Text;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Owns the on-demand image-proxy cache at wwwroot/cache/images/proxy — the transient
/// copies ImageController downloads when a client views artwork whose permanent,
/// item-keyed copy has not been cached yet. Files are keyed by SHA256(url), which is
/// NOT correlatable with media items, so lifecycle must be handled here at the URL
/// level: the download queue deletes a URL's proxy copy the moment the permanent copy
/// lands, and the daily sweep expires whatever is left by age (a proxy hit refreshes
/// the file's mtime, so entries still in active use survive the sweep).
/// </summary>
public interface IProxyImageStore
{
    /// <summary>Absolute path the proxy copy of this URL lives at (whether or not it exists).</summary>
    string GetCachedFilePath(string url);

    /// <summary>Negative-cache sentinel path for a proxy file path ("{file}.404").</summary>
    string GetSentinelPath(string cachedFilePath);

    /// <summary>
    /// Deterministic thumbnail key for a proxied URL, for <see cref="IThumbnailService"/>
    /// file naming. Derived from the URL hash, so it never collides with media-item ids.
    /// </summary>
    Guid GetThumbnailKey(string url);

    /// <summary>
    /// Refresh the file's mtime on a cache hit so the age-based sweep treats the entry
    /// as in-use (LRU semantics). Best-effort.
    /// </summary>
    void TouchOnHit(string cachedFilePath);

    /// <summary>
    /// Delete the proxy copy of a URL along with its negative-cache sentinel and any
    /// derived thumbnails. Called when the permanent item-keyed copy of the same image
    /// has been cached and the DB now points at it. Returns files deleted.
    /// </summary>
    int DeleteCachedCopy(string url);

    /// <summary>
    /// Delete every proxy file (images and .404 sentinels) whose mtime is older than
    /// <paramref name="maxAge"/>, plus the thumbnails derived from each expired image.
    /// Returns files deleted.
    /// </summary>
    int SweepExpired(TimeSpan maxAge);
}

public class ProxyImageStore : IProxyImageStore
{
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<ProxyImageStore> _logger;
    private readonly string _dir;

    public ProxyImageStore(IWebHostEnvironment env, IThumbnailService thumbnails, ILogger<ProxyImageStore> logger)
    {
        _thumbnails = thumbnails;
        _logger = logger;

        var webRoot = !string.IsNullOrEmpty(env.WebRootPath)
            ? env.WebRootPath
            : Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        _dir = Path.Combine(webRoot, "cache", "images", "proxy");
        Directory.CreateDirectory(_dir);
    }

    public string GetCachedFilePath(string url)
        => Path.Combine(_dir, HashUrl(url) + GetExtensionFromUrl(url));

    public string GetSentinelPath(string cachedFilePath) => cachedFilePath + ".404";

    public Guid GetThumbnailKey(string url) => ThumbnailKeyFromHash(HashUrl(url));

    public void TouchOnHit(string cachedFilePath)
    {
        try { File.SetLastWriteTimeUtc(cachedFilePath, DateTime.UtcNow); }
        catch { /* mtime refresh is an optimisation for the sweep, never worth failing a request */ }
    }

    public int DeleteCachedCopy(string url)
    {
        var deleted = 0;
        try
        {
            var hash = HashUrl(url);
            // Glob "{hash}.*" rather than the single extension-guessed name: it also picks
            // up the ".404" sentinel and any historical extension drift for the same URL.
            foreach (var file in Directory.GetFiles(_dir, hash + ".*"))
            {
                File.Delete(file);
                deleted++;
            }
            deleted += _thumbnails.DeleteThumbnails(ThumbnailKeyFromHash(hash));
            if (deleted > 0)
            {
                _logger.LogDebug("Deleted {Count} proxy cache file(s) for {Url}", deleted, url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete proxy cache copy for {Url}", url);
        }
        return deleted;
    }

    public int SweepExpired(TimeSpan maxAge)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            var cutoff = DateTime.UtcNow - maxAge;

            foreach (var file in Directory.GetFiles(_dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                    File.Delete(file);
                    deleted++;

                    // The filename stem IS the URL hash, so the derived thumbnail key is
                    // recoverable even though the original URL is long forgotten.
                    var stem = Path.GetFileName(file);
                    var dot = stem.IndexOf('.');
                    if (dot > 0) stem = stem[..dot];
                    if (stem.Length == 64)
                    {
                        deleted += _thumbnails.DeleteThumbnails(ThumbnailKeyFromHash(stem));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sweep proxy cache file {Path}", file);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Proxy image sweep removed {Count} file(s) older than {Age}", deleted, maxAge);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proxy image sweep failed");
        }
        return deleted;
    }

    private static string HashUrl(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    /// <summary>
    /// Must stay byte-for-byte compatible with the historical ImageController derivation
    /// (guid = first 16 bytes of SHA256 over the UPPERCASE hex hash string), or existing
    /// thumbnails on installed servers become unreachable orphans.
    /// </summary>
    private static Guid ThumbnailKeyFromHash(string urlHashHex)
    {
        var guidBytes = new byte[16];
        Array.Copy(SHA256.HashData(Encoding.UTF8.GetBytes(urlHashHex)), guidBytes, 16);
        return new Guid(guidBytes);
    }

    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 5 &&
                (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp"))
            {
                return ext;
            }
        }
        catch { }
        return ".jpg"; // Default
    }
}
