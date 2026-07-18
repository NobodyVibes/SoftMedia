import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import GlobalSearchResults from './GlobalSearchResults';
import { MediaType, type MediaItem } from '../../types';
import type { GlobalSearchResult } from '../../services/searchService';

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

function group(libraryType: GlobalSearchResult['libraryType'], items: MediaItem[]): GlobalSearchResult {
    return { libraryId: 'lib-1', libraryName: 'Lib', libraryType, items };
}

const onClose = vi.fn();

beforeEach(() => vi.clearAllMocks());

/** R-WI-017 — play routing (regression-locks the dead `/player/` route bug) and
 *  the new track/episode result handling. */
describe('GlobalSearchResults', () => {
    it('movie play navigates to the video player at /play (not the dead /player route)', () => {
        const movie = item({ id: 'm1', title: 'A Movie', type: MediaType.Movie });
        render(<GlobalSearchResults results={[group('Movie', [movie])]} isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.querySelector('svg') && b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockNavigate).toHaveBeenCalledWith('/play/m1');
        expect(mockPlayTrack).not.toHaveBeenCalled();
        expect(onClose).toHaveBeenCalled();
    });

    it('track play starts the audio player instead of navigating', () => {
        const track = item({ id: 't1', title: 'A Song', type: MediaType.Audio });
        render(<GlobalSearchResults results={[group('Music', [track])]} isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockPlayTrack).toHaveBeenCalledWith(track);
        expect(mockNavigate).not.toHaveBeenCalled();
    });

    it('album play opens the detail page (albums are not directly streamable)', () => {
        const album = item({ id: 'a1', title: 'An Album', type: MediaType.Album });
        render(<GlobalSearchResults results={[group('Music', [album])]} isLoading={false} onClose={onClose} />);

        const [playBtn] = screen.getAllByRole('button').filter(b => b.className.includes('rounded-full'));
        fireEvent.click(playBtn);

        expect(mockNavigate).toHaveBeenCalledWith('/media/a1');
        expect(mockPlayTrack).not.toHaveBeenCalled();
    });

    it('episode ROW click goes to the series page (episodes have no working detail page)', () => {
        const ep = item({ id: 'e1', title: 'Pilot', type: MediaType.Episode, seriesId: 's9', seasonNumber: 1, episodeNumber: 1 });
        render(<GlobalSearchResults results={[group('TV', [ep])]} isLoading={false} onClose={onClose} />);

        fireEvent.click(screen.getByText('Pilot'));
        expect(mockNavigate).toHaveBeenCalledWith('/media/s9');
    });

    it('duplicate track titles are disambiguated by artist/album context', () => {
        const t1 = item({ id: 't1', title: 'Same Song', type: MediaType.Audio, metadata: { artist: 'Band A', album: 'Album A' } });
        const t2 = item({ id: 't2', title: 'Same Song', type: MediaType.Audio, metadata: { artist: 'Band B', album: 'Album B' } });
        render(<GlobalSearchResults results={[group('Music', [t1, t2])]} isLoading={false} onClose={onClose} />);

        expect(screen.getByText('Band A — Album A')).toBeInTheDocument();
        expect(screen.getByText('Band B — Album B')).toBeInTheDocument();
    });

    it('episode rows show series + S/E context', () => {
        const ep = item({
            id: 'e1', title: 'Pilot', type: MediaType.Episode, seriesId: 's9',
            seasonNumber: 2, episodeNumber: 5, metadata: { seriesTitle: 'Some Show' },
        });
        render(<GlobalSearchResults results={[group('TV', [ep])]} isLoading={false} onClose={onClose} />);

        expect(screen.getByText('Some Show — S2 · E5')).toBeInTheDocument();
    });
});
