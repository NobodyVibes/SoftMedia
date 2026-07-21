import { render, screen, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent } from '@testing-library/react';
import LoadingImage from './LoadingImage';
import { useAuthStore } from '../../store/authStore';

vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: true }),
}));

/**
 * Regression cover for the "poster art disappears after the page sits idle" bug.
 *
 * Media URLs carry the reduced-privilege MEDIA token, which rotates on its own
 * (~2h) schedule. LoadingImage subscribed to the ACCESS token instead, so a media
 * token that arrived late or rotated never re-rendered the <img>: the element kept
 * a stale (or token-less) URL, 401'd, latched `status === 'error'`, and rendered
 * its fallback — the artwork vanished with no way back short of a full reload.
 */
describe('LoadingImage — media token rotation', () => {
    beforeEach(() => {
        useAuthStore.setState({ token: 'access-tok', mediaToken: 'media-tok-1' });
    });

    const src = '/api/v1/music/album/abc/cover?width=300';

    it('embeds the media token, not the access token', () => {
        render(<LoadingImage src={src} alt="cover" />);
        const img = screen.getByAltText('cover');
        expect(img.getAttribute('src')).toContain('access_token=media-tok-1');
        expect(img.getAttribute('src')).not.toContain('access-tok');
    });

    it('rebuilds the URL when the media token rotates', () => {
        render(<LoadingImage src={src} alt="cover" />);
        expect(screen.getByAltText('cover').getAttribute('src')).toContain('media-tok-1');

        act(() => {
            useAuthStore.setState({ mediaToken: 'media-tok-2' });
        });

        const img = screen.getByAltText('cover');
        expect(img.getAttribute('src')).toContain('access_token=media-tok-2');
        expect(img.getAttribute('src')).not.toContain('media-tok-1');
    });

    it('recovers a failed image once a valid media token arrives', () => {
        // The window this reproduces: the URL is built with no token at all, which
        // is what attachAuthToApiUrl emits when the media token has not resolved.
        useAuthStore.setState({ mediaToken: null });
        render(<LoadingImage src={src} alt="cover" fallback={<div>no art</div>} />);

        const img = screen.getByAltText('cover');
        expect(img.getAttribute('src')).not.toContain('access_token');

        // The server rejects the token-less request; the component latches 'error'
        // and swaps in the fallback — this is the state the user saw.
        fireEvent.error(img);
        expect(screen.queryByAltText('cover')).toBeNull();
        expect(screen.getByText('no art')).toBeTruthy();

        // The media token resolving must clear that latched failure and retry.
        act(() => {
            useAuthStore.setState({ mediaToken: 'media-tok-late' });
        });

        const retried = screen.getByAltText('cover');
        expect(retried.getAttribute('src')).toContain('access_token=media-tok-late');
        expect(screen.queryByText('no art')).toBeNull();
    });

    it('does NOT reset a failed image when only the access token rotates', () => {
        render(<LoadingImage src={src} alt="cover" fallback={<div>no art</div>} />);
        fireEvent.error(screen.getByAltText('cover'));
        expect(screen.getByText('no art')).toBeTruthy();

        // An access-token refresh is unrelated to media URLs; retrying on it just
        // re-requests a URL that is still bad. The media token is the real signal.
        act(() => {
            useAuthStore.setState({ token: 'access-tok-2' });
        });

        expect(screen.getByText('no art')).toBeTruthy();
    });
});
