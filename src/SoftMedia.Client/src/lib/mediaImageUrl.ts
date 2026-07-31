import { getUrlToken } from '../store/authStore';

/**
 * Append `?access_token=<jwt>` to a SoftMedia image URL so that `<img src="…">`
 * loads pass authentication. Browsers cannot attach the `Authorization:
 * Bearer` header to `<img>` requests, so the server's JwtBearer
 * `OnMessageReceived` handler lifts the query-string token into
 * `context.Token` for a specific set of paths (see
 * Extensions/ServiceCollectionExtensions.cs in the backend).
 *
 * Covers API image routes AND statically served artwork under
 * `/cache/images/**` (token-gated since AA-WI-001; the append preserves
 * existing query strings like playlist covers' `?v=` cache stamp). Anything
 * else — app-relative assets, external URLs — is returned unchanged, and the
 * function NEVER rewrites the path itself (see the test pinning this).
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
    if (!url.startsWith('/api/v1/') && !url.startsWith('/cache/images/')) return url;
    const token = getUrlToken();
    if (!token) return url;

    // Use URL with a synthetic base so URLSearchParams handles the parsing
    // and `set` replaces any existing access_token rather than appending.
    const parsed = new URL(url, 'http://_softmedia');
    parsed.searchParams.set('access_token', token);
    return parsed.pathname + parsed.search;
}

/**
 * Resolve any artwork path a player/queue/playlist item may carry into a
 * fetchable `<img src>`: SoftMedia paths (API routes and the token-gated
 * `/cache/images/**` statics, AA-WI-001) get the media token attached,
 * external http(s) URLs pass through, and a missing path falls back to the
 * bundled placeholder. Replaces five identical private `getImageUrl` helpers
 * that used to special-case `/cache/` as "needs no token".
 */
export function resolveArtworkUrl(path: string | null | undefined, fallback = '/placeholder-music.png'): string {
    if (!path) return fallback;
    if (path.startsWith('http')) return path;
    return attachAuthToApiUrl(path);
}

/**
 * Append a width query parameter to a URL that supports it, for server-side
 * thumbnail generation. Supports both the music endpoint and the image proxy;
 * other URLs keep their size. All SoftMedia URLs (API and /cache/images)
 * get the query-string token attached.
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
