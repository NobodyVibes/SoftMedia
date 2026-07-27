import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import PlaylistDetailPage from './PlaylistDetailPage';
import { playlistService } from '../services/playlistService';
import { libraryService } from '../services/libraryService';

vi.mock('../services/playlistService', () => ({
    playlistService: { get: vi.fn(), update: vi.fn(), delete: vi.fn(), reorder: vi.fn(), removeItem: vi.fn() },
}));
vi.mock('../services/libraryService', () => ({
    libraryService: { getAll: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock('../store/audioStore', () => ({
    useAudioStore: (sel: (s: unknown) => unknown) => sel({ playPlaylist: vi.fn() }),
}));
vi.mock('../components/playlists/SortablePlaylistItem', () => ({
    SortablePlaylistItem: () => <div />,
}));

const getMock = vi.mocked(playlistService.get);
const librariesMock = vi.mocked(libraryService.getAll);

const renderPage = (entry = '/playlists/p1') => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={[entry]}>
                <Routes>
                    <Route path="/playlists/:id" element={<PlaylistDetailPage />} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
};

const backHref = async () => {
    const link = await screen.findByRole('link', { name: /Back/i });
    return () => link.getAttribute('href');
};

// The label is the shared BackButton's fixed "Back"; what matters here is where
// it points, which is unchanged.
describe('PlaylistDetailPage back link', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getMock.mockResolvedValue({
            id: 'p1',
            name: 'Road Trip',
            description: null,
            isPublic: false,
            isOwner: true,
            ownerUsername: 'me',
            createdAt: '2026-01-01',
            updatedAt: '2026-01-01',
            items: [],
            kind: 'Manual',
            rules: null,
            coverImagePath: null,
        });
    });

    // The reported bug: this link pointed at "/playlists", which is not a route,
    // so App's catch-all redirected to the home page.
    it('points at the Music library playlists tab, not /playlists or home', async () => {
        librariesMock.mockResolvedValue([
            { id: 'movies', name: 'Movies', type: 'Movie', paths: [], order: 0 },
            { id: 'tunes', name: 'Music', type: 'Music', paths: [], order: 1 },
        ]);

        renderPage();

        const link = await screen.findByRole('link', { name: /Back/i });
        await waitFor(() =>
            expect(link.getAttribute('href')).toBe('/libraries/tunes?view=playlists')
        );
        expect(link.getAttribute('href')).not.toBe('/playlists');
        expect(link.getAttribute('href')).not.toBe('/');
    });

    it('falls back to home only when the server has no Music library', async () => {
        librariesMock.mockResolvedValue([
            { id: 'movies', name: 'Movies', type: 'Movie', paths: [], order: 0 },
        ]);

        renderPage();

        const link = await screen.findByRole('link', { name: /Back/i });
        await waitFor(() => expect(link.getAttribute('href')).toBe('/'));
    });

    describe('with several Music libraries', () => {
        const twoMusicLibraries = [
            { id: 'vinyl', name: 'Vinyl Rips', type: 'Music' as const, paths: [], order: 0 },
            { id: 'flac', name: 'FLAC', type: 'Music' as const, paths: [], order: 1 },
        ];

        it('returns to the library the playlist was opened from', async () => {
            librariesMock.mockResolvedValue(twoMusicLibraries);

            renderPage('/playlists/p1?from=flac');

            const href = await backHref();
            await waitFor(() => expect(href()).toBe('/libraries/flac?view=playlists'));
        });

        it('guesses the first Music library when opened without an origin', async () => {
            librariesMock.mockResolvedValue(twoMusicLibraries);

            renderPage('/playlists/p1');

            const href = await backHref();
            await waitFor(() => expect(href()).toBe('/libraries/vinyl?view=playlists'));
        });

        it('ignores an origin pointing at a library that no longer exists', async () => {
            librariesMock.mockResolvedValue(twoMusicLibraries);

            renderPage('/playlists/p1?from=deleted-lib');

            const href = await backHref();
            await waitFor(() => expect(href()).toBe('/libraries/vinyl?view=playlists'));
        });
    });
});
