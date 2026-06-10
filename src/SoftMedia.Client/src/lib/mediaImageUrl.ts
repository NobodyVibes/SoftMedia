import { getUrlToken } from '../store/authStore';

/**
 * Append `?access_token=<jwt>` to an API image URL so that `<img src="…">`
 * loads pass authentication. Browsers cannot attach the `Authorization:
 * Bearer` header to `<img>` requests, so the server's JwtBearer
 * `OnMessageReceived` handler lifts the query-string token into
 * `context.Token` for a specific set of paths (see
 * Extensions/ServiceCollectionExtensions.cs in the backend).
 *
 * Only SoftMedia API URLs are modified — static `/cache/*` assets and
 * external URLs are returned unchanged.
 *
 * **Idempotent** — if the URL already carries an `access_token`, it is
 * replaced with the current store value rather than duplicated. Previously
 * the function unconditionally appended, which produced
 * `?access_token=OLD&access_token=NEW` after a token refresh; ASP.NET Core
 * collapses that to "OLD,NEW" and the JWT validator rejects it as
 * malformed → 401. Callers (MediaCard / LoadingImage) double-invoke this
 * function via the URL transformation chain, so idempotency is mandatory.
 */
export function attachAuthToApiUrl(url: string): string {
    if (!url.startsWith('/api/v1/')) return url;
    const token = getUrlToken();
    if (!token) return url;

    // Use URL with a synthetic base so URLSearchParams handles the parsing
    // and `set` replaces any existing access_token rather than appending.
    const parsed = new URL(url, 'http://_softmedia');
    parsed.searchParams.set('access_token', token);
    return parsed.pathname + parsed.search;
}

/**
 * Append a width query parameter to a URL that supports it, for server-side
 * thumbnail generation. Supports both the music endpoint and the image proxy;
 * returns other URLs (e.g. /cache/* static files) unchanged. Also attaches
 * the query-string access token for API URLs.
 */
function withWidth(url: string, width: number): string {
    if (url.includes('/api/v1/music/') || url.includes('/api/v1/image/proxy')) {
        const separator = url.includes('?') ? '&' : '?';
        return attachAuthToApiUrl(`${url}${separator}width=${width}`);
    }
    return attachAuthToApiUrl(url);
}

/**
 * Resolve the card-sized poster URL for a MediaCard (~192px rendered).
 * Keep this in sync with the URL logic inside MediaCard so prefetching
 * actually warms the cache for the URL the card will request.
 */
export function resolveCardPosterUrl(posterPath: string | null | undefined): string | null | undefined {
    if (!posterPath) return posterPath;
    return withWidth(posterPath, 300);
}

/**
 * Resolve the hero/detail-page poster URL (~320px rendered).
 * Uses a larger thumbnail for retina quality on the sidebar poster.
 */
export function resolveHeroPosterUrl(posterPath: string | null | undefined): string | null | undefined {
    if (!posterPath) return posterPath;
    return withWidth(posterPath, 500);
}

/**
 * Resolve the detail-page backdrop URL. The backdrop is blurred and scaled,
 * so a small thumbnail is plenty — requesting a large backdrop wastes bandwidth
 * and blocks other image downloads.
 */
export function resolveBackdropUrl(backdropPath: string | null | undefined): string | null | undefined {
    if (!backdropPath) return backdropPath;
    return withWidth(backdropPath, 800);
}
