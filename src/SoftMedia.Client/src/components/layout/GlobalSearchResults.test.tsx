import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import GlobalSearchResults from './GlobalSearchResults';
import { MediaType, type MediaItem } from '../../types';
import type { GlobalSearchResult } from '../../services/searchService';
import type { PlaylistSummary } from '../../services/playlistService';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => ({
    ...(await vi.importActual('react-router-dom')),
    useNavigate: () => mockNavigate,
}));

const mockPlayTrack = vi.fn();
vi.mock('../../store/audioStore', () => ({
    useAudioStore: (selector: (s: { playTrack: typeof mockPlayTrack }) => unknown) =>
        selector({ playTrack: mockPlayTrack }),
}));

function item(overrides: Partial<MediaItem>): MediaItem {
    return {
        id: 'id-1',
        title: 'Item',
        sortTitle: 'Item',
        libraryId: 'lib-1',
        dateAdded: new Date().toISOString(),
        ...overrides,
    } as MediaItem;
}

function group(
    libraryType: GlobalSearchResult['libraryType'],
    items: MediaItem[],
    overrides: Partial<GlobalSearchResult> = {},
): GlobalSearchResult {
    return {
        libraryId: 'lib-1', libraryName: 'Lib', libraryType, items,
        bestMatchTier: 1, matchReasons: {},
        ...overrides,
    };
}

function playlistFixture(overrides: Partial<PlaylistSummary> = {}): PlaylistSummary {
    return {
        id: 'p1', name: 'Road Trip', description: null, isPublic: false, isOwner: true,
        ownerUsername: 'me', itemCount: 12, createdAt: '2026-01-01', updatedAt: '2026-01-01',
        coverImagePaths: [], kind: 'Manual', rules: null, coverImagePath: null,
        ...overrides,
    };
}

const onClose = vi.fn();

beforeEach(() => vi.clearAllMocks());

/** R-WI-017 — play routing (regression-locks the dead `/player/` route bug) and
 *  the new track/episode result handling. */
describe('GlobalSearchResults', () => {
    it('movie play navigates to the video player at /play (not the dead /player route)', () => {
        const movie = item({ id: 'm1', title: 'A Movie', type: MediaType.Movie });
        render(<GlobalSearchResults results={[group('Movie', [movie])]} query="test" isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.querySelector('svg') && b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockNavigate).toHaveBeenCalledWith('/play/m1');
        expect(mockPlayTrack).not.toHaveBeenCalled();
        expect(onClose).toHaveBeenCalled();
    });

    it('track play starts the audio player instead of navigating', () => {
        const track = item({ id: 't1', title: 'A Song', type: MediaType.Audio });
        render(<GlobalSearchResults results={[group('Music', [track])]} query="test" isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockPlayTrack).toHaveBeenCalledWith(track);
        expect(mockNavigate).not.toHaveBeenCalled();
    });

    it('album play opens the detail page (albums are not directly streamable)', () => {
        const album = item({ id: 'a1', title: 'An Album', type: MediaType.Album });
        render(<GlobalSearchResults results={[group('Music', [album])]} query="test" isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockNavigate).toHaveBeenCalledWith('/media/a1');
        expect(mockPlayTrack).not.toHaveBeenCalled();
    });

    it('episode ROW click goes to the series page (episodes have no working detail page)', () => {
        const ep = item({ id: 'e1', title: 'Pilot', type: MediaType.Episode, seriesId: 's9', seasonNumber: 1, episodeNumber: 1 });
        render(<GlobalSearchResults results={[group('TV', [ep])]} query="test" isLoading={false} onClose={onClose} />);

        fireEvent.click(screen.getByText('Pilot'));
        expect(mockNavigate).toHaveBeenCalledWith('/media/s9');
    });

    it('duplicate track titles are disambiguated by artist/album context', () => {
        const t1 = item({ id: 't1', title: 'Same Song', type: MediaType.Audio, metadata: { artist: 'Band A', album: 'Album A' } });
        const t2 = item({ id: 't2', title: 'Same Song', type: MediaType.Audio, metadata: { artist: 'Band B', album: 'Album B' } });
        render(<GlobalSearchResults results={[group('Music', [t1, t2])]} query="test" isLoading={false} onClose={onClose} />);

        expect(screen.getByText('Band A — Album A')).toBeInTheDocument();
        expect(screen.getByText('Band B — Album B')).toBeInTheDocument();
    });

    it('episode rows show series + S/E context', () => {
        const ep = item({
            id: 'e1', title: 'Pilot', type: MediaType.Episode, seriesId: 's9',
            seasonNumber: 2, episodeNumber: 5, metadata: { seriesTitle: 'Some Show' },
        });
        render(<GlobalSearchResults results={[group('TV', [ep])]} query="test" isLoading={false} onClose={onClose} />);

        expect(screen.getByText('Some Show — S2 · E5')).toBeInTheDocument();
    });
});

/**
 * Playlists come from their own endpoint and render as their own group: they are
 * not media items and belong to no library, so they cannot sit inside one.
 */
describe('GlobalSearchResults — playlists', () => {
    const playlist = (overrides: Partial<PlaylistSummary> = {}): PlaylistSummary => ({
        id: 'p1',
        name: 'Road Trip',
        description: null,
        isPublic: false,
        isOwner: true,
        ownerUsername: 'me',
        itemCount: 12,
        createdAt: '2026-01-01',
        updatedAt: '2026-01-01',
        coverImagePaths: [],
        kind: 'Manual',
        rules: null,
        coverImagePath: null,
        ...overrides,
    });

    it('lists playlist hits under their own heading', () => {
        render(
            <GlobalSearchResults results={[]} playlists={[playlist()]} query="test" isLoading={false} onClose={onClose} />
        );

        expect(screen.getByText('Playlists')).toBeInTheDocument();
        expect(screen.getByText('Road Trip')).toBeInTheDocument();
    });

    // /media/{id} is not a playlist route — it would render a media-shaped shell.
    it('opens the playlist page, not a media page', () => {
        render(
            <GlobalSearchResults results={[]} playlists={[playlist()]} query="test" isLoading={false} onClose={onClose} />
        );

        fireEvent.click(screen.getByText('Road Trip'));

        expect(mockNavigate).toHaveBeenCalledWith('/playlists/p1');
        expect(onClose).toHaveBeenCalled();
    });

    it('attributes a playlist shared by someone else', () => {
        render(
            <GlobalSearchResults
                results={[]}
                playlists={[playlist({ isOwner: false, isPublic: true, ownerUsername: 'dana' })]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(screen.getByText('Shared by dana')).toBeInTheDocument();
    });

    it('marks automatic playlists', () => {
        render(
            <GlobalSearchResults
                results={[]}
                playlists={[playlist({ kind: 'Smart', name: 'Most Played' })]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(screen.getByText('Automatic playlist')).toBeInTheDocument();
    });

    // Media hits and playlist hits arrive from separate requests; a query that
    // matches only a playlist must not fall through to "No results found".
    it('does not report an empty search when only playlists match', () => {
        render(
            <GlobalSearchResults results={[]} playlists={[playlist()]} query="test" isLoading={false} onClose={onClose} />
        );

        expect(screen.queryByText('No results found')).not.toBeInTheDocument();
    });

    it('still reports an empty search when nothing matches at all', () => {
        render(<GlobalSearchResults results={[]} playlists={[]} query="test" isLoading={false} onClose={onClose} />);

        expect(screen.getByText('No results found')).toBeInTheDocument();
    });
});

/** Placement is match quality, not result type — the pinned-playlists era is over. */
describe('GlobalSearchResults — unified ranking', () => {
    const headings = () =>
        Array.from(document.querySelectorAll('.uppercase')).map(el => el.textContent?.trim());

    it('puts an exact-title media group above a description-only playlist hit', () => {
        const movie = item({ id: 'm1', title: 'Test', type: MediaType.Movie });
        render(
            <GlobalSearchResults
                results={[group('Movie', [movie], { bestMatchTier: 0 })]}
                playlists={[playlistFixture({ name: 'Road Mix', description: 'test songs' })]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(headings()).toEqual(['Lib', 'Playlists']);
    });

    it('puts a name-matched playlist above a weakly matched media group', () => {
        const movie = item({ id: 'm1', title: 'Some Film', type: MediaType.Movie });
        render(
            <GlobalSearchResults
                results={[group('Movie', [movie], { bestMatchTier: 2 })]}
                playlists={[playlistFixture({ name: 'Test Mix' })]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(headings()).toEqual(['Playlists', 'Lib']);
    });

    it('surfaces a library by its name and navigates to it', () => {
        render(
            <GlobalSearchResults
                results={[]}
                playlists={[]}
                libraries={[{ id: 'lib-test', name: 'Test', type: 'Movie', paths: [], order: 0 }]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        fireEvent.click(screen.getByText('Test'));

        expect(mockNavigate).toHaveBeenCalledWith('/libraries/lib-test');
        expect(onClose).toHaveBeenCalled();
    });

    it('shows the match reason where an item has no context of its own', () => {
        const movie = item({ id: 'm1', title: 'Some Film', type: MediaType.Movie });
        render(
            <GlobalSearchResults
                results={[group('Movie', [movie], {
                    bestMatchTier: 2,
                    matchReasons: { m1: 'Matched cast: Ted Testa' },
                })]}
                playlists={[]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(screen.getByText('Matched cast: Ted Testa')).toBeInTheDocument();
    });

    // A track's artist—album line already explains its presence; the reason must
    // not displace real context.
    it('prefers artist/album context over the match reason', () => {
        const track = item({
            id: 't1', title: 'Opening Song', type: MediaType.Audio,
            metadata: { artist: 'Band', album: 'Test Sessions' },
        });
        render(
            <GlobalSearchResults
                results={[group('Music', [track], {
                    bestMatchTier: 2,
                    matchReasons: { t1: 'Matched album: Test Sessions' },
                })]}
                playlists={[]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(screen.getByText('Band — Test Sessions')).toBeInTheDocument();
        expect(screen.queryByText('Matched album: Test Sessions')).not.toBeInTheDocument();
    });

    it('shows results when only a library name matches', () => {
        render(
            <GlobalSearchResults
                results={[]}
                playlists={[]}
                libraries={[{ id: 'lib-test', name: 'Test', type: 'Movie', paths: [], order: 0 }]}
                query="test"
                isLoading={false}
                onClose={onClose}
            />
        );

        expect(screen.queryByText('No results found')).not.toBeInTheDocument();
        expect(screen.getByText('Libraries')).toBeInTheDocument();
    });
});
