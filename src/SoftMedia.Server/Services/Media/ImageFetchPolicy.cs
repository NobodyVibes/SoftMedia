namespace SoftMedia.Server.Services.Media;

/// <summary>
/// MC-WI-002 — the single authority for which remote hosts artwork may be fetched from
/// and how redirects are chased. Both the scan-time downloader (ImageCacheService) and
/// the on-demand proxy (ImageController) previously carried private copies of this
/// policy, and they drifted: audit wave-2 L-26 tightened the downloader's archive.org
/// suffix while the proxy kept the broad one. Shared code makes the next tightening
/// apply everywhere at once.
/// </summary>
public static class ImageFetchPolicy
{
    /// <summary>Allowed URL hosts (allowlist for SSRF prevention).</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // TVMaze
        "static.tvmaze.com",
        // MusicBrainz / Cover Art Archive
        "coverartarchive.org",
        "archive.org",
        // Wikidata / Wikimedia
        "upload.wikimedia.org",
        "commons.wikimedia.org",
        // OMDb (posters hosted on Amazon)
        "m.media-amazon.com",
        "ia.media-imdb.com",
        // OpenLibrary
        "covers.openlibrary.org"
    };

    /// <summary>Allowed image content types for downloaded artwork.</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp"
    };

    // Maximum redirect hops to follow before giving up.
    public const int MaxRedirects = 5;

    /// <summary>
    /// A host is allowed if it is an exact allowlist member OR an Internet Archive
    /// STORAGE node. Cover Art Archive "front" URLs 302/307-redirect to a per-release IA
    /// storage node (iaNNN.us.archive.org / dnNNNNNN.ca.archive.org). Audit wave-2 L-26:
    /// the suffix is anchored on ".us.archive.org" / ".ca.archive.org" (the documented
    /// storage patterns) rather than the broad ".archive.org", so it admits the genuine
    /// CAA targets but NOT web.archive.org — the Wayback Machine, a content-rewriting
    /// fetch proxy that could launder an arbitrary upstream fetch through an allowlisted
    /// host. Look-alikes ("evilarchive.org") and internal SSRF targets never matched the
    /// dot-anchored suffix.
    /// </summary>
    public static bool IsHostAllowed(string host) =>
        AllowedHosts.Contains(host)
        || host.EndsWith(".us.archive.org", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ca.archive.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// T6.5/I-8 — the INITIAL url must pass the same scheme guard as redirect hops;
    /// don't rely on HttpClient to reject non-http(s) schemes downstream.
    /// </summary>
    public static bool TryValidateUrl(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// Issues a GET and follows up to <see cref="MaxRedirects"/> redirects MANUALLY,
    /// re-validating each hop's host against the allowlist and scheme guard. Callers'
    /// HttpClients have AllowAutoRedirect=false, so without this a 3xx from an
    /// allowlisted host would not be chased — and a malicious/compromised allowlisted
    /// host can't use a redirect to reach an internal address (cloud metadata, loopback,
    /// RFC1918), because every Location is re-checked. Returns null when the chain
    /// leaves the allowlist or exceeds the hop limit.
    /// </summary>
    public static async Task<HttpResponseMessage?> GetWithAllowlistedRedirectsAsync(
        HttpClient client, string url, ILogger logger, CancellationToken ct = default)
    {
        var currentUrl = url;
        for (var hop = 0; ; hop++)
        {
            var response = await client.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400)
                return response; // not a redirect — success or error, caller decides

            // Redirect: validate the target before following it.
            var location = response.Headers.Location;
            response.Dispose();

            if (hop >= MaxRedirects)
            {
                logger.LogWarning("Image redirect chain exceeded {Max} hops for {Url}", MaxRedirects, url);
                return null;
            }
            if (location == null)
            {
                logger.LogWarning("Image redirect with no Location header from {Url}", currentUrl);
                return null;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(new Uri(currentUrl), location);
            if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                || !IsHostAllowed(next.Host))
            {
                logger.LogWarning("Blocked image redirect to non-allowlisted target {Target} (from {Url})", next, currentUrl);
                return null;
            }

            currentUrl = next.AbsoluteUri;
        }
    }
}
