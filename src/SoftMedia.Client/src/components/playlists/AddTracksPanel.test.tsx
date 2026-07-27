import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AddTracksPanel } from './AddTracksPanel';
import { searchService } from '../../services/searchService';
import { playlistService } from '../../services/playlistService';
import { MediaType, type MediaItem } from '../../types';

vi.mock('../../services/searchService', () => ({
    searchService: { globalSearch: vi.fn() },
}));
vi.mock('../../services/playlistService', () => ({
    playlistService: { addItems: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const searchMock = vi.mocked(searchService.globalSearch);
const addItemsMock = vi.mocked(playlistService.addItems);

const track = (id: string, title: string, type: MediaType = MediaType.Audio) =>
    ({ id, title, type, posterPath: undefined, metadata: { artist: 'Someone' } } as unknown as MediaItem);

const group = (items: MediaItem[]) => [{
    libraryId: 'lib', libraryName: 'Music', libraryType: 'Music' as const, items,
    bestMatchTier: 0, matchReasons: {},
}];

const renderPanel = (existing: string[] = []) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <AddTracksPanel playlistId="p1" existingMediaItemIds={existing} onClose={vi.fn()} />
        </QueryClientProvider>
    );
};

const search = (text: string) =>
    fireEvent.change(screen.getByLabelText('Search for tracks to add'), { target: { value: text } });

/**
 * Push past the input's debounce. Wrapped in act() because the timer firing is
 * what schedules the query's state updates — leaving it unwrapped makes every
 * test log a React act warning and buries genuine ones.
 */
const settleDebounce = async () => {
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });
};

/**
 * Before this panel, a playlist's own page could not add anything to it — the
 * empty state described a button on a different page. These tests pin the parts
 * that make it usable: audio-only results, adding without navigating away, and
 * duplicates staying possible because the data model allows them on purpose.
 */
describe('AddTracksPanel', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        vi.useFakeTimers({ shouldAdvanceTime: true });
        addItemsMock.mockResolvedValue(undefined);
    });

    it('does not search until the query is long enough to be meaningful', async () => {
        renderPanel();
        search('a');
        await settleDebounce();

        expect(searchMock).not.toHaveBeenCalled();
        expect(screen.getByText(/at least two characters/i)).toBeTruthy();
    });

    it('lists matching tracks once the query passes the threshold', async () => {
        searchMock.mockResolvedValue(group([track('t1', 'Blue Monday')]));
        renderPanel();

        search('blue');
        await settleDebounce();

        expect(await screen.findByText('Blue Monday')).toBeTruthy();
        expect(searchMock).toHaveBeenCalledWith('blue', 25);
    });

    // The global search spans every media type, but the server rejects non-audio
    // playlist items by design — offering a movie here could only produce a 400.
    it('filters out results that are not audio tracks', async () => {
        searchMock.mockResolvedValue(group([
            track('t1', 'Blue Monday'),
            track('m1', 'Blue Velvet', MediaType.Movie),
        ]));
        renderPanel();

        search('blue');
        await settleDebounce();

        expect(await screen.findByText('Blue Monday')).toBeTruthy();
        expect(screen.queryByText('Blue Velvet')).toBeNull();
    });

    it('adds a track without closing the panel, so several can be added in a row', async () => {
        searchMock.mockResolvedValue(group([track('t1', 'Blue Monday')]));
        renderPanel();

        search('blue');
        await settleDebounce();
        fireEvent.click(await screen.findByRole('button', { name: 'Add Blue Monday' }));

        await waitFor(() => expect(addItemsMock).toHaveBeenCalledWith('p1', ['t1']));
        // Still open, still showing the result.
        expect(screen.getByText('Blue Monday')).toBeTruthy();
    });

    it('marks a track already in the playlist as added', async () => {
        searchMock.mockResolvedValue(group([track('t1', 'Blue Monday')]));
        renderPanel(['t1']);

        search('blue');
        await settleDebounce();

        expect(await screen.findByText('Added')).toBeTruthy();
    });

    // Duplicates are deliberate in the data model (PlaylistItem has a surrogate
    // key precisely so a track can appear twice), so "Added" reports state
    // without blocking a second add.
    it('still allows adding a track that is already present', async () => {
        searchMock.mockResolvedValue(group([track('t1', 'Blue Monday')]));
        renderPanel(['t1']);

        search('blue');
        await settleDebounce();
        fireEvent.click(await screen.findByRole('button', { name: 'Add Blue Monday again' }));

        await waitFor(() => expect(addItemsMock).toHaveBeenCalledWith('p1', ['t1']));
    });

    it('reports an empty result set', async () => {
        searchMock.mockResolvedValue([]);
        renderPanel();

        search('zzzz');
        await settleDebounce();

        expect(await screen.findByText(/No tracks match/)).toBeTruthy();
    });
});
