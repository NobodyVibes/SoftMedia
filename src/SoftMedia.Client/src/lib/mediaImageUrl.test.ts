import { describe, it, expect, beforeEach } from 'vitest';
import { useAuthStore } from '../store/authStore';
import { attachAuthToApiUrl, resolveCardPosterUrl } from './mediaImageUrl';

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
        useAuthStore.setState({ token: null });
    });

    it('appends access_token when missing', () => {
        useAuthStore.setState({ token: 'tok-1' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo');
        expect(out).toBe('/api/v1/image/proxy?url=foo&access_token=tok-1');
    });

    it('replaces existing access_token rather than duplicating', () => {
        useAuthStore.setState({ token: 'tok-2' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo&access_token=tok-1');

        // Only one access_token should remain, with the current token's value.
        expect(out).toContain('access_token=tok-2');
        expect(out).not.toContain('access_token=tok-1');
        // Defensive: at most one occurrence of "access_token="
        expect(out.match(/access_token=/g)?.length ?? 0).toBe(1);
    });

    it('is idempotent under repeated application', () => {
        useAuthStore.setState({ token: 'tok-3' });
        const once = attachAuthToApiUrl('/api/v1/image/proxy?url=foo');
        const twice = attachAuthToApiUrl(once);
        const thrice = attachAuthToApiUrl(twice);
        expect(thrice).toBe(once);
    });

    it('passes non-API URLs through unchanged', () => {
        useAuthStore.setState({ token: 'tok-4' });
        expect(attachAuthToApiUrl('https://example.com/poster.jpg'))
            .toBe('https://example.com/poster.jpg');
        expect(attachAuthToApiUrl('/cache/images/something.jpg'))
            .toBe('/cache/images/something.jpg');
    });

    it('returns URL unchanged when no token present', () => {
        useAuthStore.setState({ token: null });
        expect(attachAuthToApiUrl('/api/v1/image/proxy?url=foo'))
            .toBe('/api/v1/image/proxy?url=foo');
    });

    it('preserves other query parameters and their order semantics', () => {
        useAuthStore.setState({ token: 'tok-5' });
        const out = attachAuthToApiUrl('/api/v1/image/proxy?url=foo&width=300');
        // url and width must still be present.
        expect(out).toContain('url=foo');
        expect(out).toContain('width=300');
        expect(out).toContain('access_token=tok-5');
    });

    it('encodes URL-unsafe characters in the token', () => {
        useAuthStore.setState({ token: 'a/b+c=' });
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
        useAuthStore.setState({ token: 'tok-card' });
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
});
