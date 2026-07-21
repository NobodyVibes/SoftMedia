import { render, act, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import CollectionStripSection from './CollectionStripSection';
import { useAuthStore } from '../../store/authStore';
import { MediaType } from '../../types';

/**
 * Companion to LoadingImage.test.tsx, covering the OTHER half of the artwork
 * problem: components that render a plain <img> instead of delegating to
 * LoadingImage. They resolve the media token into the URL at render time, so
 * without a subscription to that token they keep serving a stale URL after a
 * rotation and the image 401s for good.
 *
 * CollectionStripSection stands in for the whole family here (CastStripItem,
 * QueueList, PersistentPlayer, the overlays, …) — they all now take the same
 * useMediaTokenRefresh subscription.
 */
vi.mock('../../services/collectionService', () => ({
    collectionService: {
        getByMovie: vi.fn(async () => ({
            id: 'col-1',
            name: 'Austin Powers',
            overview: null,
            posterUrl: null,
            isAuto: true,
            items: [
                {
                    isCurrent: false,
                    media: {
                        id: 'm1',
                        title: 'Goldmember',
                        year: 2002,
                        type: MediaType.Movie,
                        // A proxied (API-served) poster — the kind that carries a token.
                        posterPath: '/api/v1/image/proxy?url=https%3A%2F%2Fexample.com%2Fp.jpg',
                    },
                },
            ],
        })),
    },
}));

function renderStrip() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter>
                <CollectionStripSection movieId="m0" />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

describe('CollectionStripSection — media token rotation', () => {
    beforeEach(() => {
        useAuthStore.setState({ token: 'access-tok', mediaToken: 'media-tok-1' });
    });

    it('rebuilds poster URLs when the media token rotates', async () => {
        // The poster is decorative (alt=""), so it carries the presentation role
        // rather than img — query the element directly.
        const { container } = renderStrip();
        const posterSrc = () => container.querySelector('img')?.getAttribute('src');

        await waitFor(() => expect(posterSrc()).toContain('access_token=media-tok-1'));

        act(() => {
            useAuthStore.setState({ mediaToken: 'media-tok-2' });
        });

        // Freshly-tokened src — changing src re-triggers the browser fetch, which
        // is what recovers an image that already 401'd.
        await waitFor(() => expect(posterSrc()).toContain('access_token=media-tok-2'));
        expect(posterSrc()).not.toContain('media-tok-1');
    });

    it('picks up a token that arrives after the first render', async () => {
        useAuthStore.setState({ mediaToken: null });
        const { container } = renderStrip();
        const posterSrc = () => container.querySelector('img')?.getAttribute('src');

        // No token yet — this is the guaranteed-401 URL shape.
        await waitFor(() => expect(posterSrc()).toBeTruthy());
        expect(posterSrc()).not.toContain('access_token');

        act(() => {
            useAuthStore.setState({ mediaToken: 'media-tok-late' });
        });

        await waitFor(() => expect(posterSrc()).toContain('access_token=media-tok-late'));
    });
});
