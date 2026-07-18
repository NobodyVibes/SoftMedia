import { render, screen, fireEvent, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MovieEndOverlay } from './MovieEndOverlay';
import type { MediaItem } from '../../types';
import type { PostPlayInfo } from '../../services/postPlayService';

const movie = (id: string, title: string): MediaItem =>
    ({ id, title, libraryId: 'lib-1', year: 2001 } as unknown as MediaItem);

const postPlay: PostPlayInfo = {
    collectionName: 'The Trilogy',
    collectionItems: [movie('film-2', 'Film Two'), movie('film-3', 'Film Three')],
    similarItems: [movie('other-1', 'Genre Mate')],
};

function renderOverlay(overrides: Partial<Parameters<typeof MovieEndOverlay>[0]> = {}) {
    const onLeave = vi.fn();
    const onWatchCredits = vi.fn();
    const onRateCurrent = vi.fn();
    const onPauseVideo = vi.fn();
    render(
        <MovieEndOverlay
            movieTitle="Finished Movie"
            postPlay={postPlay}
            onRateCurrent={onRateCurrent}
            onWatchCredits={onWatchCredits}
            onLeave={onLeave}
            onPauseVideo={onPauseVideo}
            libraryId="lib-1"
            {...overrides}
        />,
    );
    return { onLeave, onWatchCredits, onRateCurrent, onPauseVideo };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

// The post-play card's load-bearing behaviors: collection films lead and are labeled with the
// collection name; a card click and the countdown expiry both leave via onLeave (the player owns
// transcode cleanup + navigation); Watch Credits dismisses without leaving.
describe('MovieEndOverlay', () => {
    it('leads with collection items under a "Next in" heading and plays a card on click', () => {
        const { onLeave } = renderOverlay();

        expect(screen.getByText('Next in The Trilogy')).toBeInTheDocument();
        // Collection films lead; the genre match fills the remaining slot.
        const cards = screen.getAllByTitle(/^Play /);
        expect(cards.map(c => c.textContent)).toEqual([
            expect.stringContaining('Film Two'),
            expect.stringContaining('Film Three'),
            expect.stringContaining('Genre Mate'),
        ]);

        fireEvent.click(screen.getByTitle('Play Film Two'));
        expect(onLeave).toHaveBeenCalledWith('/play/film-2');
    });

    it('auto-returns to the movie\'s library when the countdown expires', () => {
        const { onLeave } = renderOverlay();

        act(() => vi.advanceTimersByTime(11_000));

        expect(onLeave).toHaveBeenCalledWith('/libraries/lib-1');
    });

    it('pausing the countdown also pauses the video and blocks auto-navigation', () => {
        const { onLeave, onPauseVideo } = renderOverlay();

        fireEvent.click(screen.getByRole('button', { name: 'Pause countdown and video' }));
        act(() => vi.advanceTimersByTime(30_000));

        expect(onPauseVideo).toHaveBeenCalledWith(true);
        expect(onLeave).not.toHaveBeenCalled();
    });

    it('Watch Credits dismisses without leaving; Back to Library leaves immediately', () => {
        const { onLeave, onWatchCredits } = renderOverlay();

        fireEvent.click(screen.getByRole('button', { name: /Watch Credits/ }));
        expect(onWatchCredits).toHaveBeenCalled();
        expect(onLeave).not.toHaveBeenCalled();

        fireEvent.click(screen.getByRole('button', { name: /Back to Library/ }));
        expect(onLeave).toHaveBeenCalledWith('/libraries/lib-1');
    });
});
