import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import PlaylistsView from './PlaylistsView';
import { playlistService } from '../../services/playlistService';

vi.mock('../../services/playlistService', () => ({
    playlistService: { list: vi.fn(), create: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const listMock = vi.mocked(playlistService.list);

const summary = (
    id: string,
    name: string,
    isOwner = true,
    coverImagePaths: string[] = [],
) => ({
    id,
    name,
    description: null,
    isPublic: false,
    isOwner,
    ownerUsername: isOwner ? 'me' : 'someone',
    itemCount: 2,
    createdAt: '2026-01-01',
    updatedAt: '2026-01-01',
    coverImagePaths,
    kind: 'Manual' as const,
    rules: null,
    coverImagePath: null,
});

const renderView = (libraryId?: string, searchQuery?: string) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <MemoryRouter>
                <PlaylistsView libraryId={libraryId} searchQuery={searchQuery} />
            </MemoryRouter>
        </QueryClientProvider>
    );
};

const described = (id: string, name: string, description: string) => ({
    ...summary(id, name),
    description,
});

/**
 * The library FilterBar's search box is always mounted, including on the
 * Playlists tab. It used to reach only the media-items query, which this view
 * does not use — so typing in it here did nothing at all.
 */
describe('PlaylistsView search', () => {
    beforeEach(() => vi.clearAllMocks());

    it('narrows the list to playlists matching the query', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip'), summary('p2', 'Dinner Party')]);
        renderView('flac', 'road');

        expect(await screen.findByText('Road Trip')).toBeTruthy();
        expect(screen.queryByText('Dinner Party')).toBeNull();
    });

    it('matches case-insensitively', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        renderView('flac', 'ROAD');

        expect(await screen.findByText('Road Trip')).toBeTruthy();
    });

    // Same fields the global playlist search matches on, so a query that finds a
    // playlist in the top bar finds it here too.
    it('matches on the description as well as the name', async () => {
        listMock.mockResolvedValue([described('p1', 'Mix', 'songs for a long drive')]);
        renderView('flac', 'drive');

        expect(await screen.findByText('Mix')).toBeTruthy();
    });

    it('shows every playlist when the query is blank or whitespace', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip'), summary('p2', 'Dinner Party')]);
        renderView('flac', '   ');

        expect(await screen.findByText('Road Trip')).toBeTruthy();
        expect(screen.getByText('Dinner Party')).toBeTruthy();
    });

    // Someone who mistyped a name has not lost their playlists, so the
    // create-your-first prompt would be the wrong thing to show.
    it('reports no matches rather than offering to create a first playlist', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        renderView('flac', 'nothing-matches-this');

        expect(await screen.findByText(/No playlists match/)).toBeTruthy();
        expect(screen.queryByText('Build your first playlist')).toBeNull();
    });

    it('filters shared playlists too', async () => {
        listMock.mockResolvedValue([
            summary('p1', 'Road Trip', true),
            summary('p2', 'Shared Jazz', false),
            summary('p3', 'Shared Rock', false),
        ]);
        renderView('flac', 'jazz');

        expect(await screen.findByText('Shared Jazz')).toBeTruthy();
        expect(screen.queryByText('Shared Rock')).toBeNull();
        expect(screen.queryByText('Road Trip')).toBeNull();
    });

    // Dropping the heading entirely would read as "the shared playlists vanished".
    it('keeps the shared section visible with its own empty message', async () => {
        listMock.mockResolvedValue([
            summary('p1', 'Road Trip', true),
            summary('p2', 'Shared Jazz', false),
        ]);
        renderView('flac', 'road');

        expect(await screen.findByText('Road Trip')).toBeTruthy();
        expect(screen.getByText('Shared on this server')).toBeTruthy();
        expect(screen.getByText(/No playlists match/)).toBeTruthy();
    });

    it('leaves the list untouched when no query is supplied at all', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip'), summary('p2', 'Dinner Party')]);
        renderView('flac');

        expect(await screen.findByText('Road Trip')).toBeTruthy();
        expect(screen.getByText('Dinner Party')).toBeTruthy();
    });
});

describe('PlaylistsView playlist links', () => {
    beforeEach(() => vi.clearAllMocks());

    // Playlists aren't owned by a library, so the origin can't be recovered from
    // the playlist itself — it has to ride on the link.
    it('stamps the hosting library onto each playlist link', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        renderView('flac');

        const link = await screen.findByRole('link', { name: /Road Trip/ });
        expect(link.getAttribute('href')).toBe('/playlists/p1?from=flac');
    });

    it('stamps the origin on shared playlists too', async () => {
        listMock.mockResolvedValue([summary('p2', 'Shared Mix', false)]);
        renderView('flac');

        const link = await screen.findByRole('link', { name: /Shared Mix/ });
        expect(link.getAttribute('href')).toBe('/playlists/p2?from=flac');
    });

    it('omits the origin when no library id is supplied', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        renderView(undefined);

        const link = await screen.findByRole('link', { name: /Road Trip/ });
        expect(link.getAttribute('href')).toBe('/playlists/p1');
    });

    it('encodes ids that need escaping', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        renderView('a b&c');

        const link = await screen.findByRole('link', { name: /Road Trip/ });
        expect(link.getAttribute('href')).toBe('/playlists/p1?from=a%20b%26c');
    });
});

/**
 * Playlist cards used to be a flat icon tile while every other card in the app
 * led with artwork. The card now borrows its tracks' covers (server-supplied on
 * the summary) and reports when the list was last touched.
 */
describe('PlaylistsView card presentation', () => {
    beforeEach(() => vi.clearAllMocks());

    it('renders the covers the server supplied', async () => {
        listMock.mockResolvedValue([
            summary('p1', 'Road Trip', true, [
                '/api/v1/music/album/a/cover',
                '/api/v1/music/album/b/cover',
                '/api/v1/music/album/c/cover',
                '/api/v1/music/album/d/cover',
            ]),
        ]);
        const { container } = renderView();

        await screen.findByText('Road Trip');
        expect(container.querySelectorAll('img')).toHaveLength(4);
    });

    it('falls back to the gradient tile for a playlist with no artwork', async () => {
        listMock.mockResolvedValue([summary('p1', 'Road Trip')]);
        const { container } = renderView();

        await screen.findByText('Road Trip');
        expect(container.querySelectorAll('img')).toHaveLength(0);
        expect(container.querySelector('.bg-brand-gradient')).not.toBeNull();
    });

    it('shows when the playlist was last updated', async () => {
        const justNow = new Date().toISOString();
        listMock.mockResolvedValue([{ ...summary('p1', 'Road Trip'), updatedAt: justNow }]);
        renderView();

        expect(await screen.findByText('just now')).toBeTruthy();
    });

    // The old empty state was a bordered box pointing at a button elsewhere on
    // the page; it now carries the action itself.
    it('offers the create action directly from the empty state', async () => {
        listMock.mockResolvedValue([]);
        renderView();

        expect(await screen.findByText('Build your first playlist')).toBeTruthy();
        // Both the header button and the empty state's own call to action.
        expect(screen.getAllByRole('button', { name: /New Playlist/i })).toHaveLength(2);
    });
});
