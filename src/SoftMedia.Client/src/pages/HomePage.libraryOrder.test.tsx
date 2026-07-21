import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import HomePage from './HomePage';
import { useAuthStore } from '../store/authStore';
import type { Library } from '../types';

/**
 * The Recently Added rows must march down the home page in the SAME order the sidebar
 * lists the libraries — the admin-configured Library.Order the API returns, which the
 * sidebar renders verbatim.
 *
 * HomePage used to re-sort by a hardcoded type ranking (Movie, TV, Music, Book) before
 * falling back to the admin order, so an admin who placed Books above Music saw the
 * sidebar honour it and the home page ignore it. The fixture below deliberately orders
 * the libraries AGAINST that old ranking; if anyone reintroduces a type sort, this
 * fails.
 */
vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: false }),
}));

vi.mock('../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn() })),
}));

// Admin order disagreeing with the old MEDIA_TYPE_ORDER on purpose:
// Books(0), Music(1), TV(2), Movies(3) — the type ranking would emit Movies first.
const LIBRARIES: Partial<Library>[] = [
    { id: 'lib-books', name: 'Books', type: 'Book', order: 0 },
    { id: 'lib-music', name: 'Music', type: 'Music', order: 1 },
    { id: 'lib-tv', name: 'TV', type: 'TV', order: 2 },
    { id: 'lib-movies', name: 'Movies', type: 'Movie', order: 3 },
];

vi.mock('../hooks/useLibrary', () => ({
    // The API returns libraries already sorted by Order; mirror that here.
    useLibraries: () => ({ data: LIBRARIES }),
    useHeroItems: () => ({ data: [], isLoading: false }),
    // One recent item per library so every row renders.
    useLibraryRecent: (libraryId: string) => ({
        data: [{
            id: `item-${libraryId}`,
            title: `Item in ${libraryId}`,
            type: 0,
        }],
        isLoading: false,
    }),
}));

// The personalized/watchlist/continue-watching rows all fetch via api or services;
// return nothing so only the Recently Added rows render.
vi.mock('../services/api', async () => {
    const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
    return {
        ...actual,
        default: { ...actual.default, get: vi.fn(async () => ({ data: [] })) },
    };
});
vi.mock('../services/continueWatchingService', () => ({
    continueWatchingService: { list: vi.fn(async () => []) },
}));
vi.mock('../services/watchlistService', () => ({
    watchlistService: { list: vi.fn(async () => []) },
}));
vi.mock('../services/userPreferencesService', () => ({
    userPreferencesService: {
        getPreferences: vi.fn(async () => ({})),
        updatePreferences: vi.fn(async () => undefined),
    },
}));

describe('HomePage — Recently Added row order', () => {
    beforeEach(() => {
        useAuthStore.setState({ token: 'tok', mediaToken: 'media-tok' });
    });

    it('renders the rows in the admin-configured library order, not by media type', async () => {
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={queryClient}>
                <MemoryRouter>
                    <HomePage />
                </MemoryRouter>
            </QueryClientProvider>
        );

        const headings = await screen.findAllByText(/^Recently Added /);
        const titles = headings.map(h => h.textContent);

        expect(titles).toEqual([
            'Recently Added Books',
            'Recently Added Music',
            'Recently Added TV',
            'Recently Added Movies',
        ]);
    });
});
