import { render, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PersistentPlayer } from './PersistentPlayer';
import { toast } from 'sonner';
import type { MediaItem } from '../../types';

const mockAudioStore = vi.fn();
vi.mock('../../store/audioStore', () => ({ useAudioStore: () => mockAudioStore() }));
vi.mock('../../store/visualizerStore', () => ({ useVisualizerStore: () => ({ isEnabled: false, toggle: vi.fn() }) }));
vi.mock('../../hooks/useAudioAnalyser', () => ({
    useAudioAnalyser: () => ({
        frequencyData: new Uint8Array(), timeDomainData: new Uint8Array(),
        isReady: false, updateData: vi.fn(), setGlobalVolume: vi.fn(),
    }),
}));
vi.mock('./visualizers', () => ({
    AudioVisualizer: () => <div />, VisualizerSelector: () => <div />,
}));
vi.mock('../ui/ScrollingText', () => ({ ScrollingText: ({ text }: { text: string }) => <span>{text}</span> }));
vi.mock('./QueueList', () => ({ QueueList: () => <div /> }));
vi.mock('../../services/api', () => ({
    API_URL: '/api/v1',
    default: { post: vi.fn().mockResolvedValue({}) },
}));
vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

const mockedToast = vi.mocked(toast);

const makeTrack = (id: string, title: string): MediaItem => ({
    id, title, sortTitle: title, dateAdded: '2023-01-01',
    type: 'Audio', path: `/m/${id}.mp3`, libraryId: 'lib1',
    metadata: { artist: 'A', album: 'B', duration: 180 },
});

const trackA = makeTrack('track-a', 'Track A');
const trackB = makeTrack('track-b', 'Track B');

function storeState(overrides: object = {}) {
    return {
        currentTrack: trackA, isPlaying: true, volume: 1, isMuted: false,
        shuffleMode: false, repeatMode: 'off', queue: [trackB], originalQueue: [trackA, trackB],
        pause: vi.fn(), resume: vi.fn(), next: vi.fn(), previous: vi.fn(),
        toggleMute: vi.fn(), toggleShuffle: vi.fn(), cycleRepeatMode: vi.fn(),
        setVolume: vi.fn(), closePlayer: vi.fn(),
        ...overrides,
    };
}

/** The active element stays the FIRST <audio> until a crossfade swap (never triggered here). */
function fireErrorOnActiveAudio(container: HTMLElement) {
    const audio = container.querySelectorAll('audio')[0];
    expect(audio).toBeTruthy();
    fireEvent(audio, new Event('error'));
}

beforeEach(() => {
    vi.clearAllMocks();
    window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
    window.HTMLMediaElement.prototype.pause = vi.fn();
    window.HTMLMediaElement.prototype.load = vi.fn();
});

/**
 * SR-WI-052 — a dead source (404, deleted file) fires 'error' on the audio element.
 * The player must toast and auto-advance, never stall the bar silently — and it must
 * stop after one full failed pass when EVERY reachable track is broken.
 */
describe('PersistentPlayer audio error handling', () => {
    it('toasts the failed track and auto-advances to the next queue item', () => {
        const state = storeState();
        mockAudioStore.mockReturnValue(state);
        const { container } = render(<PersistentPlayer />);

        fireErrorOnActiveAudio(container);

        expect(mockedToast.error).toHaveBeenCalledWith('Couldn\'t play "Track A"');
        expect(state.next).toHaveBeenCalledTimes(1);
        expect(state.pause).not.toHaveBeenCalled();
    });

    it('stops after a full failed pass instead of advancing forever', () => {
        // Pass size is captured at the FIRST failure: current + 1 queued = 2.
        const first = storeState();
        mockAudioStore.mockReturnValue(first);
        const { container, rerender } = render(<PersistentPlayer />);

        fireErrorOnActiveAudio(container);
        expect(first.next).toHaveBeenCalledTimes(1);

        // The store advanced to Track B with nothing left queued; it errors too.
        const second = storeState({ currentTrack: trackB, queue: [], originalQueue: [trackA, trackB] });
        mockAudioStore.mockReturnValue(second);
        rerender(<PersistentPlayer />);

        fireErrorOnActiveAudio(container);

        expect(mockedToast.error).toHaveBeenCalledWith('Couldn\'t play "Track B"');
        expect(mockedToast.error).toHaveBeenCalledWith(
            'Playback stopped — none of the queued tracks could be played.'
        );
        expect(second.next).not.toHaveBeenCalled(); // stopped, no further advance
        expect(second.pause).toHaveBeenCalledTimes(1);
    });

    it('does not loop a broken track under repeat-one — it stops with a toast', () => {
        const state = storeState({ repeatMode: 'one', queue: [], originalQueue: [trackA] });
        mockAudioStore.mockReturnValue(state);
        const { container } = render(<PersistentPlayer />);

        fireErrorOnActiveAudio(container);

        expect(mockedToast.error).toHaveBeenCalledWith('Couldn\'t play "Track A"');
        expect(state.pause).toHaveBeenCalledTimes(1);
        expect(state.next).not.toHaveBeenCalled();
    });

    it('stops gracefully on a broken LAST track without the queue-wide toast', () => {
        const state = storeState({ queue: [], originalQueue: [trackA] });
        mockAudioStore.mockReturnValue(state);
        const { container } = render(<PersistentPlayer />);

        fireErrorOnActiveAudio(container);

        expect(mockedToast.error).toHaveBeenCalledTimes(1); // only "Couldn't play …"
        expect(mockedToast.error).toHaveBeenCalledWith('Couldn\'t play "Track A"');
        expect(state.pause).toHaveBeenCalledTimes(1);
        expect(state.next).not.toHaveBeenCalled();
    });
});
