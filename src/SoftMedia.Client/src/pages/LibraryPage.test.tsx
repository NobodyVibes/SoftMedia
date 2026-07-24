import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LibraryPage from './LibraryPage';
import { useAuthStore } from '../store/authStore';
import { MediaType } from '../types';
import api from '../services/api';
import { toast } from 'sonner';

vi.mock('../hooks/useMediaHub', () => ({ useMediaHub: vi.fn() }));

vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: false }),
}));

vi.mock('sonner', () => ({
    toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() },
}));

// The real FilterBar drags in its own queries; the page contract under test is
// just that it hands FilterBar a working onRescan.
vi.mock('../components/library/FilterBar', () => ({
    FilterBar: ({ onRescan }: { onRescan: () => void }) => (
        <button type="button" onClick={onRescan}>Rescan</button>
    ),
}));
vi.mock('../components/library/PhotoLibraryView', () => ({ default: () => null }));
vi.mock('../components/playlists/PlaylistsView', () => ({ default: () => null }));

vi.mock('../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn(), playPlaylist: vi.fn() })),
}));

vi.mock('../services/api', async () => {
    const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
    return { ...actual, default: { ...actual.default, get: vi.fn(), post: vi.fn() } };
});

const mockGet = api.get as unknown as ReturnType<typeof vi.fn>;
const mockPost = api.post as unknown as ReturnType<typeof vi.fn>;

class ResizeObserverStub {
    observe() { /* noop */ }
    unobserve() { /* noop */ }
    disconnect() { /* noop */ }
}

function renderPage() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter initialEntries={['/libraries/lib1']}>
                <Routes>
                    <Route path="/libraries/:id" element={<main><LibraryPage /></main>} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
}

describe('LibraryPage', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        vi.stubGlobal('ResizeObserver', ResizeObserverStub);
        useAuthStore.setState({ token: 'tok', mediaToken: 'media-tok' });

        mockGet.mockImplementation((url: string) => {
            if (url === '/libraries/lib1') {
                return Promise.resolve({ data: { id: 'lib1', name: 'Movies', type: 'Movie' } });
            }
            if (url === '/libraries/lib1/items') {
                return Promise.resolve({
                    data: {
                        items: [
                            { id: 'm1', title: 'Alpha', type: MediaType.Movie },
                            { id: 'm2', title: 'Beta', type: MediaType.Movie },
                        ],
                        totalCount: 2,
                        page: 1,
                        pageSize: 50,
                    },
                });
            }
            return Promise.resolve({ data: {} });
        });
    });

    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('surfaces a toast when kicking off a rescan fails (SR-WI-052)', async () => {
        mockPost.mockRejectedValue(new Error('boom'));
        const consoleError = vi.spyOn(console, 'error').mockImplementation(() => { /* keep output clean */ });
        renderPage();

        fireEvent.click(await screen.findByRole('button', { name: 'Rescan' }));

        await waitFor(() => {
            expect(toast.error).toHaveBeenCalledWith('Could not start the library scan. Please try again.');
        });
        expect(mockPost).toHaveBeenCalledWith('/libraries/lib1/scan');
        consoleError.mockRestore();
    });

    it('does not toast when the rescan kicks off fine (SignalR owns the progress toasts)', async () => {
        mockPost.mockResolvedValue({ data: {} });
        renderPage();

        fireEvent.click(await screen.findByRole('button', { name: 'Rescan' }));

        await waitFor(() => expect(mockPost).toHaveBeenCalledWith('/libraries/lib1/scan'));
        expect(toast.error).not.toHaveBeenCalled();
    });
});
