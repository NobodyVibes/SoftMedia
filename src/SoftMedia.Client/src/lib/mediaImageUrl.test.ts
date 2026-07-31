import { describe, it, expect, beforeEach } from 'vitest';
import { useAuthStore } from '../store/authStore';
import { attachAuthToApiUrl, resolveArtworkUrl, resolveCardPosterUrl } from './mediaImageUrl';

/**
 * Regression — the previous implementation unconditionally appended
 * `&access_token=<token>` whether one was already present or not. After a
 * token refresh, the URL chain was producing
 *   /api/v1/image/proxy?url=X&access_token=OLD&access_token=NEW
 * and ASP.NET Core's StringValues collapsed that to "OLD,NEW" before the
 * JWT validator saw it → 401.
 *
 * The function must be idempotent and the freshest token always wins.
 */
describe('attachAuthToApiUrl', () => {
    beforeEach(() => {
        useAuthStore.setState({ mediaToken: null });
    });

    it('appends access_token when missing', () => {
        useAuthStore.setState({ mediaToken: 'tok-1' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo');
        expect(out).toBe('/api/v1/image/proxy?url=foo&access_token=tok-1');
    });

    it('replaces existing access_token rather than duplicating', () => {
        useAuthStore.setState({ mediaToken: 'tok-2' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo&access_token=tok-1');

        // Only one access_token should remain, with the current token's value.
        expect(out).toContain('access_token=tok-2');
        expect(out).not.toContain('access_token=tok-1');
        // Defensive: at most one occurrence of "access_token="
        expect(out.match(/access_token=/g)?.length ?? 0).toBe(1);
    });

    it('is idempotent under repeated application', () => {
        useAuthStore.setState({ mediaToken: 'tok-3' });
        const once = attachAuthToApiUrl('/api/v1/image/proxy?url=foo');
        const twice = attachAuthToApiUrl(once);
        const thrice = attachAuthToApiUrl(twice);
        expect(thrice).toBe(once);
    });

    it('passes external and non-SoftMedia URLs through unchanged', () => {
        useAuthStore.setState({ mediaToken: 'tok-4' });
        expect(attachAuthToApiUrl('https://example.com/poster.jpg'))
            .toBe('https://example.com/poster.jpg');
        expect(attachAuthToApiUrl('/placeholder-music.png'))
            .toBe('/placeholder-music.png');
    });

    // AA-WI-001/004 — /cache/images statics are token-gated; the helper now
    // tokenizes them exactly like API routes, preserving existing query strings
    // (playlist covers carry a ?v= cache stamp).
    it('tokenizes /cache/images statics and preserves their query string', () => {
        useAuthStore.setState({ mediaToken: 'tok-cache' });
        expect(attachAuthToApiUrl('/cache/images/movies/x_poster.jpg'))
            .toBe('/cache/images/movies/x_poster.jpg?access_token=tok-cache');
        const cover = attachAuthToApiUrl('/cache/images/playlists/p.jpg?v=123');
        expect(cover).toContain('v=123');
        expect(cover).toContain('access_token=tok-cache');
    });

    it('returns URL unchanged when no token present', () => {
        useAuthStore.setState({ mediaToken: null });
        expect(attachAuthToApiUrl('/api/v1/image/proxy?url=foo'))
            .toBe('/api/v1/image/proxy?url=foo');
    });

    it('preserves other query parameters and their order semantics', () => {
        useAuthStore.setState({ mediaToken: 'tok-5' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo&width=300');
        // url and width must still be present.
        expect(out).toContain('url=foo');
        expect(out).toContain('width=300');
        expect(out).toContain('access_token=tok-5');
    });

    it('encodes URL-unsafe characters in the token', () => {
        useAuthStore.setState({ mediaToken: 'a/b+c=' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo');
        // URLSearchParams encodes deterministically; just confirm the literal
        // unsafe characters are not present in the final query.
        expect(out).not.toContain('access_token=a/b+c=');
        // %2F = '/', %2B = '+'. Decoding via URLSearchParams should round-trip.
        const params = new URLSearchParams(out.split('?')[1]);
        expect(params.get('access_token')).toBe('a/b+c=');
    });
});

describe('resolveCardPosterUrl (integration through the full chain)', () => {
    beforeEach(() => {
        useAuthStore.setState({ mediaToken: 'tok-card' });
    });

    it('produces a single access_token even when called repeatedly', () => {
        const once = resolveCardPosterUrl('/api/v1/image/proxy?url=https%3A%2F%2Fexample.com%2Fp.jpg');
        // Pass the result back through resolveCardPosterUrl — simulates the
        // double-application pattern that LoadingImage triggers.
        const twice = resolveCardPosterUrl(once);
        expect(twice?.match(/access_token=/g)?.length ?? 0).toBe(1);
    });

    it('produces a URL with width=300 and the current token', () => {
        const out = resolveCardPosterUrl('/api/v1/image/proxy?url=https%3A%2F%2Fexample.com%2Fp.jpg');
        expect(out).toContain('width=300');
        expect(out).toContain('access_token=tok-card');
    });

    /**
     * Locally cached posters are served as static files from wwwroot, NOT from
     * an API route. The collection strip and CollectionDetailPage each carried a
     * private resolveImageUrl() whose fallback branch prefixed `${API_URL}`,
     * turning `/cache/images/movies/x_poster.jpg` into
     * `/api/v1/cache/images/movies/x_poster.jpg` → 404 → broken-image icon.
     * Scanned movies store exactly this shape in MediaItems.PosterUrl, so every
     * card in those two views was broken. The invariant since AA-WI-004: the
     * helper may APPEND a token but must never rewrite the PATH.
     */
    it('tokenizes a locally cached /cache poster path without rewriting it', () => {
        const cached = '/cache/images/movies/0efd0034-4876-4612-aa8b-f82f426b15ae_poster.jpg';
        const out = resolveCardPosterUrl(cached);
        expect(out).toContain(cached);
        expect(out).toContain('access_token=tok-card');
        expect(out).not.toContain('/api/v1/cache');
    });
});

describe('resolveArtworkUrl (shared player/queue artwork resolver)', () => {
    beforeEach(() => {
        useAuthStore.setState({ mediaToken: 'tok-art' });
    });

    it('falls back to the placeholder for missing paths', () => {
        expect(resolveArtworkUrl(undefined)).toBe('/placeholder-music.png');
        expect(resolveArtworkUrl(null)).toBe('/placeholder-music.png');
    });

    it('tokenizes API and /cache/images paths, passes external URLs through', () => {
        expect(resolveArtworkUrl('/api/v1/music/x/cover')).toContain('access_token=tok-art');
        expect(resolveArtworkUrl('/cache/images/music/x_cover.jpg')).toContain('access_token=tok-art');
        expect(resolveArtworkUrl('https://cdn.example/x.jpg')).toBe('https://cdn.example/x.jpg');
    });
});
