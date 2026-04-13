/**
 * Append a width query parameter to a URL that supports it, for server-side
 * thumbnail generation. Supports both the music endpoint and the image proxy;
 * returns other URLs (e.g. /cache/* static files) unchanged.
 */
function withWidth(url: string, width: number): string {
    if (url.includes('/api/v1/music/') || url.includes('/api/v1/image/proxy')) {
        const separator = url.includes('?') ? '&' : '?';
        return `${url}${separator}width=${width}`;
    }
    return url;
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
