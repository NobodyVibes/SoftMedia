import { render, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { PersistentPlayer } from './PersistentPlayer';
import api from '../../services/api';
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

const mockedPost = vi.mocked(api.post);

const track: MediaItem = {
    id: 'track-1', title: 'Song', sortTitle: 'Song', dateAdded: '2023-01-01',
    type: 'Audio', path: '/m/song.mp3', libraryId: 'lib1',
    metadata: { artist: 'A', album: 'B', duration: 180 },
};

function storeState(overrides: object = {}) {
    return {
        currentTrack: track, isPlaying: true, volume: 1, isMuted: false,
        shuffleMode: false, repeatMode: 'off', queue: [],
        pause: vi.fn(), resume: vi.fn(), next: vi.fn(), previous: vi.fn(),
        toggleMute: vi.fn(), toggleShuffle: vi.fn(), cycleRepeatMode: vi.fn(),
        setVolume: vi.fn(), closePlayer: vi.fn(),
        ...overrides,
    };
}

function setMediaTime(el: HTMLMediaElement, currentTime: number, duration: number) {
    Object.defineProperty(el, 'currentTime', { value: currentTime, configurable: true, writable: true });
    Object.defineProperty(el, 'duration', { value: duration, configurable: true });
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedPost.mockResolvedValue({} as never);
    window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
    window.HTMLMediaElement.prototype.pause = vi.fn();
    window.HTMLMediaElement.prototype.load = vi.fn();
});

afterEach(() => vi.useRealTimers());

/**
 * R-WI-013 — the music player must report listen beats (it previously reported nothing, so
 * music history stayed empty). The server applies threshold/dedup; the client just beats.
 */
describe('PersistentPlayer progress beats', () => {
    it('track end posts a final beat at the full position (play completes server-side)', () => {
        const state = storeState();
        mockAudioStore.mockReturnValue(state);
        const { container } = render(<PersistentPlayer />);

        const audios = container.querySelectorAll('audio');
        expect(audios.length).toBeGreaterThan(0);
        audios.forEach(a => setMediaTime(a, 180, 180));
        audios.forEach(a => fireEvent(a, new Event('ended')));

        expect(mockedPost).toHaveBeenCalledWith('/interaction/track-1/progress', { position: 180 });
        expect(state.next).toHaveBeenCalled(); // playback flow unaffected
    });

    it('beats are throttled to ~10s of listening and skip the first tick after a track change', () => {
        vi.useFakeTimers();
        vi.setSystemTime(new Date('2026-07-17T12:00:00Z'));
        mockAudioStore.mockReturnValue(storeState());
        const { container } = render(<PersistentPlayer />);
        const audios = [...container.querySelectorAll('audio')];

        audios.forEach(a => setMediaTime(a, 5, 180));
        audios.forEach(a => fireEvent(a, new Event('timeupdate')));
        expect(mockedPost).not.toHaveBeenCalled(); // first tick only stamps the throttle

        vi.setSystemTime(new Date('2026-07-17T12:00:11Z'));
        audios.forEach(a => setMediaTime(a, 16, 180));
        audios.forEach(a => fireEvent(a, new Event('timeupdate')));

        expect(mockedPost).toHaveBeenCalledWith('/interaction/track-1/progress', { position: 16 });
        expect(mockedPost).toHaveBeenCalledTimes(1); // one beat, not one per timeupdate
    });

    it('never posts while paused', () => {
        vi.useFakeTimers();
        vi.setSystemTime(new Date('2026-07-17T12:00:00Z'));
        mockAudioStore.mockReturnValue(storeState({ isPlaying: false }));
        const { container } = render(<PersistentPlayer />);
        const audios = [...container.querySelectorAll('audio')];

        audios.forEach(a => { setMediaTime(a, 5, 180); fireEvent(a, new Event('timeupdate')); });
        vi.setSystemTime(new Date('2026-07-17T12:00:20Z'));
        audios.forEach(a => { setMediaTime(a, 5, 180); fireEvent(a, new Event('timeupdate')); });

        expect(mockedPost).not.toHaveBeenCalled();
    });
});
