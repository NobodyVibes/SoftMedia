import { render, screen, fireEvent, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PersistentPlayer } from './PersistentPlayer';
import { useAudioStore } from '../../store/audioStore';
import type { MediaItem } from '../../types';

// Real audioStore on purpose — this is about whether skip actually advances it.
vi.mock('../../store/visualizerStore', () => ({
    useVisualizerStore: () => ({ isEnabled: false, toggle: vi.fn() }),
}));
vi.mock('../../hooks/useAudioAnalyser', () => ({
    useAudioAnalyser: () => ({
        frequencyData: new Uint8Array(), timeDomainData: new Uint8Array(),
        isReady: false, updateData: vi.fn(), setGlobalVolume: vi.fn(),
    }),
}));
vi.mock('./visualizers', () => ({
    AudioVisualizer: () => <div />, VisualizerSelector: () => <div />,
}));
vi.mock('../ui/ScrollingText', () => ({
    ScrollingText: ({ text }: { text: string }) => <span>{text}</span>,
}));
vi.mock('./QueueList', () => ({ QueueList: () => <div /> }));
vi.mock('../../services/api', () => ({
    API_URL: '/api/v1',
    default: { post: vi.fn().mockResolvedValue({}) },
}));

const track = (n: number): MediaItem => ({
    id: `t${n}`,
    title: `Track ${n}`,
    sortTitle: `Track ${n}`,
    dateAdded: '2026-01-01',
    type: 'Audio',
    metadata: { artist: 'Artist' },
    libraryId: 'lib1',
});

const nextButton = () => screen.getAllByLabelText('Next track')[0];
const prevButton = () => screen.getAllByLabelText('Previous track')[0];
const currentTitle = () => useAudioStore.getState().currentTrack?.title;

describe('PersistentPlayer skip-next', () => {
    beforeEach(() => {
        window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
        window.HTMLMediaElement.prototype.pause = vi.fn();
        window.HTMLMediaElement.prototype.load = vi.fn();

        useAudioStore.setState({
            currentTrack: track(1),
            queue: [track(2), track(3)],
            originalQueue: [track(1), track(2), track(3)],
            history: [],
            isPlaying: true,
            repeatMode: 'off',
            shuffleMode: false,
            volume: 1,
            isMuted: false,
        });
    });

    it('advances to the next queued track', () => {
        render(<PersistentPlayer />);
        act(() => { fireEvent.click(nextButton()); });
        expect(currentTitle()).toBe('Track 2');
    });

    it('advances again on a second press', () => {
        render(<PersistentPlayer />);
        act(() => { fireEvent.click(nextButton()); });
        act(() => { fireEvent.click(nextButton()); });
        expect(currentTitle()).toBe('Track 3');
    });

    // The reported symptom: next appears dead while previous still works.
    it('skips forward while repeat-one is engaged', () => {
        useAudioStore.setState({ repeatMode: 'one' });
        render(<PersistentPlayer />);

        act(() => { fireEvent.click(nextButton()); });
        expect(currentTitle()).toBe('Track 2');
    });

    it('previous still steps back under repeat-one', () => {
        useAudioStore.setState({ repeatMode: 'one', history: [track(0)] });
        render(<PersistentPlayer />);

        act(() => { fireEvent.click(prevButton()); });
        expect(currentTitle()).toBe('Track 0');
    });

    it('skips forward while shuffle is engaged', () => {
        useAudioStore.setState({ shuffleMode: true });
        render(<PersistentPlayer />);

        act(() => { fireEvent.click(nextButton()); });
        expect(currentTitle()).not.toBe('Track 1');
    });

    // Not a bug, but the other way Next can look dead: a single-track play
    // (playTrack) deliberately replaces the queue, so there is nothing to skip
    // to. Playback stops rather than the track changing.
    it('stops instead of advancing when the queue is empty and repeat is off', () => {
        useAudioStore.setState({ queue: [] });
        render(<PersistentPlayer />);

        act(() => { fireEvent.click(nextButton()); });

        expect(currentTitle()).toBe('Track 1');
        expect(useAudioStore.getState().isPlaying).toBe(false);
    });

    it('wraps to the start of the queue under repeat-all when the queue drains', () => {
        useAudioStore.setState({ repeatMode: 'all', queue: [] });
        render(<PersistentPlayer />);

        act(() => { fireEvent.click(nextButton()); });
        expect(currentTitle()).toBe('Track 1');
        expect(useAudioStore.getState().isPlaying).toBe(true);
    });

    it('completes the crossfade path when the next track is already buffered', () => {
        vi.useFakeTimers();
        try {
            render(<PersistentPlayer />);

            // The inactive element signals it is ready, which arms the crossfade.
            const audios = document.querySelectorAll('audio');
            act(() => { fireEvent.canPlayThrough(audios[1]); });

            act(() => { fireEvent.click(nextButton()); });
            act(() => { vi.advanceTimersByTime(500); });

            expect(currentTitle()).toBe('Track 2');
        } finally {
            vi.useRealTimers();
        }
    });
});
