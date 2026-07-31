import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueueList } from './QueueList';
import type { MediaItem } from '../../types';

const mockStore = vi.fn();
const jumpToQueueIndex = vi.fn();
const reorderQueue = vi.fn();

vi.mock('../../store/audioStore', () => ({
    useAudioStore: () => mockStore(),
}));

vi.mock('../../hooks/useMediaTokenRefresh', () => ({
    useMediaTokenRefresh: () => { /* no token rotation under test */ },
}));

vi.mock('../../lib/mediaImageUrl', () => ({
    attachAuthToApiUrl: (url: string) => url,
    resolveArtworkUrl: (url: string | null | undefined) => url ?? '/placeholder-music.png',
}));

vi.mock('../ui/ScrollingText', () => ({
    ScrollingText: ({ text }: { text: string }) => <span>{text}</span>,
}));

const makeTrack = (n: number): MediaItem => ({
    id: `track-${n}`,
    title: `Track ${n}`,
    sortTitle: `Track ${n}`,
    dateAdded: '2026-01-01',
    type: 'Audio',
    metadata: { artist: 'Prolific Artist' },
    libraryId: 'lib1',
});

const makeQueue = (count: number) => Array.from({ length: count }, (_, i) => makeTrack(i + 1));

const rowLabels = () =>
    screen.getAllByLabelText(/^Play Track /).map((el) => el.getAttribute('aria-label'));

describe('QueueList paging', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mockStore.mockReturnValue({
            queue: makeQueue(120),
            currentTrack: null,
            reorderQueue,
            jumpToQueueIndex,
        });
    });

    // "Play all" on an artist with a deep back catalogue used to mount every row
    // at once, each with a dnd-kit sortable and an artwork <img>.
    it('renders only one page of a several-hundred track queue', () => {
        render(<QueueList />);

        expect(rowLabels()).toHaveLength(50);
        expect(screen.getByLabelText(/^Play Track 1$/)).toBeTruthy();
        expect(screen.queryByLabelText(/^Play Track 51$/)).toBeNull();
        expect(screen.getByText(/1.50 of 120/)).toBeTruthy();
    });

    it('advances a page and keeps absolute queue positions', () => {
        render(<QueueList />);

        fireEvent.click(screen.getByLabelText('Next page of queue'));

        expect(rowLabels()).toHaveLength(50);
        expect(screen.getByLabelText(/^Play Track 51$/)).toBeTruthy();
        expect(screen.queryByLabelText(/^Play Track 50$/)).toBeNull();
        expect(screen.getByText(/51.100 of 120/)).toBeTruthy();

        // Row numbers continue from the real queue position rather than resetting.
        expect(screen.getByText('51')).toBeTruthy();
    });

    it('plays the absolute queue index when a row on a later page is clicked', () => {
        render(<QueueList />);

        fireEvent.click(screen.getByLabelText('Next page of queue'));
        fireEvent.click(screen.getByLabelText('Play Track 51'));

        // Zero-based: Track 51 lives at queue index 50.
        expect(jumpToQueueIndex).toHaveBeenCalledWith(50);
    });

    it('shows the final partial page and stops there', () => {
        render(<QueueList />);
        const next = screen.getByLabelText('Next page of queue');

        fireEvent.click(next);
        fireEvent.click(next);

        expect(rowLabels()).toHaveLength(20);
        expect(screen.getByText(/101.120 of 120/)).toBeTruthy();
        expect(next.hasAttribute('disabled')).toBe(true);
    });

    it('walks back to the first page', () => {
        render(<QueueList />);

        fireEvent.click(screen.getByLabelText('Next page of queue'));
        fireEvent.click(screen.getByLabelText('Previous page of queue'));

        expect(screen.getByLabelText(/^Play Track 1$/)).toBeTruthy();
        expect(screen.getByLabelText('Previous page of queue').hasAttribute('disabled')).toBe(true);
    });

    it('hides the pager for a queue that fits on one page', () => {
        mockStore.mockReturnValue({
            queue: makeQueue(20),
            currentTrack: null,
            reorderQueue,
            jumpToQueueIndex,
        });

        render(<QueueList />);

        expect(rowLabels()).toHaveLength(20);
        expect(screen.queryByLabelText('Next page of queue')).toBeNull();
    });

    it('falls back to the last reachable page as the queue drains', () => {
        const { rerender } = render(<QueueList />);

        fireEvent.click(screen.getByLabelText('Next page of queue'));
        fireEvent.click(screen.getByLabelText('Next page of queue'));
        expect(screen.getByText(/101.120 of 120/)).toBeTruthy();

        // Tracks played through; page 3 no longer exists. Clamping on read keeps
        // this from stranding the user on a blank page.
        mockStore.mockReturnValue({
            queue: makeQueue(60),
            currentTrack: null,
            reorderQueue,
            jumpToQueueIndex,
        });
        rerender(<QueueList />);

        expect(screen.getByText(/51.60 of 60/)).toBeTruthy();
        expect(rowLabels()).toHaveLength(10);
    });
});
