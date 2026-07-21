import { render, screen, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import BrowsePage from './BrowsePage';
import { useAuthStore } from '../store/authStore';
import { MediaType } from '../types';
import api from '../services/api';

vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: false }),
}));

vi.mock('../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn() })),
}));

vi.mock('../services/api', async () => {
    const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
    return { ...actual, default: { ...actual.default, get: vi.fn() } };
});

const mockGet = api.get as unknown as ReturnType<typeof vi.fn>;

function page(items: Array<{ id: string; title: string }>, totalCount = items.length) {
    return {
        data: {
            items: items.map(i => ({ ...i, type: MediaType.Movie })),
            totalCount,
            page: 1,
            pageSize: 50,
        },
    };
}

/**
 * The page issues TWO different GETs: the grid hits /browse and the FilterBar's genre
 * picker hits /browse/genres, which returns a bare string[]. Route by URL rather than
 * resolving one shape for everything — handing the picker a PagedResult makes it call
 * .filter on an object and crash the render.
 */
function respondWith(gridResult: unknown, genres: string[] = ['Comedy', 'Drama']) {
    mockGet.mockImplementation((url: string) => {
        if (url === '/browse/genres') return Promise.resolve({ data: genres });
        return gridResult instanceof Error
            ? Promise.reject(gridResult)
            : Promise.resolve(gridResult);
    });
}

/** The grid request specifically — its position among the calls is not guaranteed. */
function gridCall() {
    return mockGet.mock.calls.find((call: unknown[]) => call[0] === '/browse');
}

function renderAt(search: string) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter initialEntries={[`/browse${search}`]}>
                <BrowsePage />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

describe('BrowsePage', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        useAuthStore.setState({ token: 'tok', mediaToken: 'media-tok' });
    });

    it('forwards the query-string criteria to the browse endpoint', async () => {
        respondWith(page([{ id: '1', title: 'Worship Music' }]));

        renderAt('?genre=Heavy%20Metal&sortBy=dateadded');

        await waitFor(() => expect(gridCall()).toBeDefined());
        const config = gridCall()![1];
        expect(config.params).toMatchObject({ genre: 'Heavy Metal', sortBy: 'dateadded', page: 1 });
        // Absent criteria must not be sent at all — an explicit undefined would
        // serialise and the server would treat it as an active filter.
        expect(config.params).not.toHaveProperty('decade');
        expect(config.params).not.toHaveProperty('unplayed');
    });

    it('titles the page from the active filters', async () => {
        respondWith(page([{ id: '1', title: 'A' }], 56));

        renderAt('?genre=Rock');

        expect(await screen.findByRole('heading', { name: 'Rock' })).toBeTruthy();
        expect(await screen.findByText('56 items')).toBeTruthy();
    });

    it.each([
        ['?decade=1990', 'From the 1990s'],
        ['?unplayed=true', 'Never Played'],
        ['?genre=Rock&decade=1990', 'Rock · 1990s'],
        ['', 'Browse'],
    ])('derives the heading %s -> %s', async (search, expected) => {
        respondWith(page([{ id: '1', title: 'A' }]));

        renderAt(search);

        expect(await screen.findByRole('heading', { name: expected })).toBeTruthy();
    });

    it('shows an empty state rather than a bare grid when nothing matches', async () => {
        respondWith(page([], 0));

        renderAt('?genre=Nonexistent');

        expect(await screen.findByText('Nothing here.')).toBeTruthy();
    });

    /**
     * Render-loop guard.
     *
     * FilterBar lists its `onGenre` callback in an effect's dependency array, so an
     * inline arrow — new identity every render — retriggers that effect on every
     * render. Each run writes to the URL, which re-renders this page, which makes
     * another new arrow. The first version of this page did exactly that and looped
     * forever: it did not fail the suite, it HUNG it, killing the vitest worker after
     * 14 minutes with "tests 0ms". A hang is far harder to read than a failure, which
     * is why this is pinned explicitly.
     *
     * Counting requests is the cheap proxy: a looping page issues them without bound.
     */
    it('settles instead of looping when the URL is seeded with filters', async () => {
        respondWith(page([{ id: '1', title: 'A' }]));

        renderAt('?genre=Rock&year=1995&search=matrix&sortBy=dateadded');

        await waitFor(() => expect(gridCall()).toBeDefined());
        // Let any cascading effects run — a loop would balloon the count here.
        // Wrapped in act so the settling updates aren't reported as unacted-on.
        await act(async () => {
            await new Promise(resolve => setTimeout(resolve, 300));
        });

        const gridCalls = mockGet.mock.calls.filter((call: unknown[]) => call[0] === '/browse');
        expect(gridCalls.length).toBeLessThanOrEqual(3);
    });

    it('surfaces a load failure instead of rendering as empty', async () => {
        respondWith(new Error('boom'));

        renderAt('?genre=Rock');

        // An error must not be indistinguishable from "no results" — that reads as
        // an empty library rather than a broken request.
        expect(await screen.findByText('Error loading results.')).toBeTruthy();
        expect(screen.queryByText('Nothing here.')).toBeNull();
    });
});
