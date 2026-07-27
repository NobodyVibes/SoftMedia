import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LibraryPage from './LibraryPage';
import { useAuthStore } from '../store/authStore';
import api from '../services/api';

vi.mock('../hooks/useMediaHub', () => ({ useMediaHub: vi.fn() }));
vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: false }),
}));
vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }));

// Expose the view-mode tabs so we can drive them.
vi.mock('../components/library/FilterBar', () => ({
    FilterBar: ({ viewMode, onViewModeChange }: {
        viewMode?: string;
        onViewModeChange?: (m: string) => void;
    }) => (
        <div>
            <span data-testid="view-mode">{viewMode ?? 'none'}</span>
            <button type="button" onClick={() => onViewModeChange?.('playlists')}>Playlists tab</button>
            <button type="button" onClick={() => onViewModeChange?.('artists')}>Artists tab</button>
        </div>
    ),
}));
vi.mock('../components/library/PhotoLibraryView', () => ({ default: () => null }));
vi.mock('../components/playlists/PlaylistsView', () => ({
    default: () => <div data-testid="playlists-view" />,
}));
vi.mock('../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn(), playPlaylist: vi.fn() })),
}));
vi.mock('../services/api', async () => {
    const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
    return { ...actual, default: { ...actual.default, get: vi.fn(), post: vi.fn() } };
});

const mockGet = api.get as unknown as ReturnType<typeof vi.fn>;

class ResizeObserverStub {
    observe() { /* noop */ }
    unobserve() { /* noop */ }
    disconnect() { /* noop */ }
}

function LocationProbe() {
    const loc = useLocation();
    return <span data-testid="search">{loc.search}</span>;
}

function renderAt(entry: string) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter initialEntries={[entry]}>
                <Routes>
                    <Route
                        path="/libraries/:id"
                        element={<main><LibraryPage /><LocationProbe /></main>}
                    />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
}

describe('LibraryPage view-mode is URL-addressable', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        vi.stubGlobal('ResizeObserver', ResizeObserverStub);
        useAuthStore.setState({ token: 'tok', mediaToken: 'media-tok' });

        mockGet.mockImplementation((url: string) => {
            if (url === '/libraries/tunes') {
                return Promise.resolve({ data: { id: 'tunes', name: 'Music', type: 'Music' } });
            }
            return Promise.resolve({
                data: { items: [], totalCount: 0, page: 1, pageSize: 50 },
            });
        });
    });

    afterEach(() => vi.unstubAllGlobals());

    // Without this the "All playlists" back link would land on the library's
    // default Artists tab rather than the playlists grid.
    it('opens the Playlists tab straight from ?view=playlists', async () => {
        renderAt('/libraries/tunes?view=playlists');
        expect(await screen.findByTestId('playlists-view')).toBeTruthy();
    });

    it('defaults to artists with no view param', async () => {
        renderAt('/libraries/tunes');
        await waitFor(() => expect(screen.getByTestId('view-mode').textContent).toBe('artists'));
        expect(screen.queryByTestId('playlists-view')).toBeNull();
    });

    // The tab bar is only wired up once the library resolves as Music, so each
    // case waits for that before driving it.
    const awaitTabsReady = (mode: string) =>
        waitFor(() => expect(screen.getByTestId('view-mode').textContent).toBe(mode));

    it('writes the tab choice back to the URL', async () => {
        renderAt('/libraries/tunes');
        await awaitTabsReady('artists');

        fireEvent.click(screen.getByText('Playlists tab'));

        await waitFor(() => expect(screen.getByTestId('search').textContent).toBe('?view=playlists'));
        expect(screen.getByTestId('playlists-view')).toBeTruthy();
    });

    it('drops the param again on the default tab rather than leaving ?view=artists', async () => {
        renderAt('/libraries/tunes?view=playlists');
        await awaitTabsReady('playlists');

        fireEvent.click(screen.getByText('Artists tab'));

        await waitFor(() => expect(screen.getByTestId('search').textContent).toBe(''));
    });

    // PhotoLibraryView keys off ?album=; switching tabs must not wipe it.
    it('preserves unrelated query params when switching tabs', async () => {
        renderAt('/libraries/tunes?album=Holiday');
        await awaitTabsReady('artists');

        fireEvent.click(screen.getByText('Playlists tab'));

        await waitFor(() => {
            const search = new URLSearchParams(screen.getByTestId('search').textContent ?? '');
            expect(search.get('album')).toBe('Holiday');
            expect(search.get('view')).toBe('playlists');
        });
    });
});
