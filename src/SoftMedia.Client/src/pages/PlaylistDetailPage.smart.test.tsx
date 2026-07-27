import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import PlaylistDetailPage from './PlaylistDetailPage';
import { playlistService, type PlaylistDetail } from '../services/playlistService';
import { libraryService } from '../services/libraryService';

vi.mock('../services/playlistService', () => ({
    playlistService: { get: vi.fn(), update: vi.fn(), delete: vi.fn(), reorder: vi.fn(), removeItem: vi.fn() },
}));
vi.mock('../services/libraryService', () => ({ libraryService: { getAll: vi.fn() } }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock('../store/audioStore', () => ({
    useAudioStore: (sel: (s: unknown) => unknown) =>
        sel({ playPlaylist: vi.fn(), addToQueue: vi.fn(), currentTrack: null }),
}));
vi.mock('../components/playlists/SortablePlaylistItem', () => ({
    SortablePlaylistItem: ({ canEdit }: { canEdit: boolean }) => (
        <div data-testid="track-row" data-can-edit={String(canEdit)} />
    ),
}));

const getMock = vi.mocked(playlistService.get);
const librariesMock = vi.mocked(libraryService.getAll);

const track = (id: string, title: string) => ({
    playlistItemId: id,
    order: 0,
    media: { id, title, type: 'Audio', durationSeconds: 100 },
});

const detail = (overrides: Partial<PlaylistDetail> = {}): PlaylistDetail => ({
    id: 'p1',
    name: 'Most Played',
    description: null,
    isPublic: false,
    isOwner: true,
    ownerUsername: 'me',
    createdAt: '2026-01-01',
    updatedAt: '2026-01-01',
    items: [track('t1', 'Song One')],
    kind: 'Smart',
    rules: { sort: 'MostPlayed', limit: 100 },
    ...overrides,
} as PlaylistDetail);

const renderPage = () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={['/playlists/p1']}>
                <Routes>
                    <Route path="/playlists/:id" element={<PlaylistDetailPage />} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
};

/**
 * A smart playlist's membership is a query the server re-runs on every read, so
 * any hand-editing control would either be rejected outright or appear to work
 * and vanish on refresh. The page must not offer them at all.
 */
describe('PlaylistDetailPage — smart playlists', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        librariesMock.mockResolvedValue([]);
    });

    it('does not offer Add tracks', async () => {
        getMock.mockResolvedValue(detail());
        renderPage();

        await screen.findByRole('heading', { name: 'Most Played' });
        expect(screen.queryByRole('button', { name: /Add tracks/i })).toBeNull();
    });

    it('does not offer the public/private toggle', async () => {
        getMock.mockResolvedValue(detail());
        renderPage();

        await screen.findByRole('heading', { name: 'Most Played' });
        expect(screen.queryByRole('switch')).toBeNull();
    });

    it('renders track rows as non-editable, so no drag handle or remove appears', async () => {
        getMock.mockResolvedValue(detail());
        renderPage();

        const row = await screen.findByTestId('track-row');
        expect(row.getAttribute('data-can-edit')).toBe('false');
    });

    it('explains what the playlist is selecting', async () => {
        getMock.mockResolvedValue(detail());
        renderPage();

        expect(await screen.findByText(/All tracks · most played first/)).toBeTruthy();
    });

    it('still allows playback', async () => {
        getMock.mockResolvedValue(detail());
        renderPage();

        expect(await screen.findByRole('button', { name: /^Play$/ })).toBeTruthy();
        expect(screen.getByRole('button', { name: /Shuffle/ })).toBeTruthy();
    });

    it('says an empty result is about the rules, not about adding tracks', async () => {
        getMock.mockResolvedValue(detail({ items: [] }));
        renderPage();

        expect(await screen.findByText(/Nothing matches this playlist's rules/)).toBeTruthy();
        expect(screen.queryByRole('button', { name: /Add tracks/i })).toBeNull();
    });

    // The server withholds rules from non-owners; the page must not assume they exist.
    it('renders for a non-owner without rules', async () => {
        getMock.mockResolvedValue(detail({ isOwner: false, rules: null, ownerUsername: 'someone' }));
        renderPage();

        await screen.findByRole('heading', { name: 'Most Played' });
        expect(screen.queryByText(/most played first/)).toBeNull();
    });

    it('leaves the controls on a manual playlist alone', async () => {
        getMock.mockResolvedValue(detail({ kind: 'Manual', rules: null, name: 'Road Trip' }));
        renderPage();

        await screen.findByRole('heading', { name: 'Road Trip' });
        expect(screen.getByRole('button', { name: /Add tracks/i })).toBeTruthy();
        expect(screen.getByRole('switch')).toBeTruthy();
        expect(screen.getByTestId('track-row').getAttribute('data-can-edit')).toBe('true');
    });
});
