import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useEffect, useState } from 'react';
import { PersistentPlayer } from './PersistentPlayer';
import type { MediaItem } from '../../types';

/**
 * Guards the <audio> callback refs.
 *
 * An inline arrow ref gets a fresh identity on every render, so React detaches
 * it (calls it with null) and re-attaches it on every commit. Mirroring the
 * element into state from inside that callback therefore scheduled a new render
 * per commit; once useAudioAnalyser started reporting isReady back down, the two
 * oscillated and React bailed with "Maximum update depth exceeded" — a blank
 * page the moment music started.
 *
 * The other PersistentPlayer suites pin the analyser mock at a constant
 * `isReady: false`, which is precisely what let the old loop settle. This mock
 * mirrors the real hook instead: readiness follows the elements it was handed.
 */
const mockAudioStore = vi.fn();
const analyserRenders = vi.fn();

vi.mock('../../store/audioStore', () => ({
    useAudioStore: () => mockAudioStore(),
}));

vi.mock('../../store/visualizerStore', () => ({
    useVisualizerStore: () => ({ isEnabled: false, toggle: vi.fn() }),
}));

vi.mock('../../hooks/useAudioAnalyser', () => ({
    useAudioAnalyser: (a: HTMLAudioElement | null, b: HTMLAudioElement | null) => {
        analyserRenders();
         
        const [isReady, setIsReady] = useState(false);
         
        useEffect(() => {
            setIsReady(!!(a && b));
        }, [a, b]);
        return {
            frequencyData: new Uint8Array(64),
            timeDomainData: new Uint8Array(256),
            isReady,
            updateData: vi.fn(),
            setGlobalVolume: vi.fn(),
        };
    },
}));

vi.mock('./visualizers', () => ({
    AudioVisualizer: () => <div data-testid="audio-visualizer" />,
    VisualizerSelector: () => <div data-testid="visualizer-selector" />,
}));

vi.mock('../ui/ScrollingText', () => ({
    ScrollingText: ({ text }: { text: string }) => <span>{text}</span>,
}));

vi.mock('./QueueList', () => ({
    QueueList: () => <div data-testid="queue-list" />,
}));

vi.mock('../../services/api', () => ({
    API_URL: 'http://localhost:5000/api',
    default: { post: vi.fn().mockResolvedValue({}) },
}));

describe('PersistentPlayer audio element refs', () => {
    const mockTrack: MediaItem = {
        id: '1',
        title: 'Test Song',
        sortTitle: 'Test Song',
        dateAdded: '2023-01-01',
        type: 'Audio',
        metadata: { artist: 'Test Artist', album: 'Test Album', duration: 180 },
        libraryId: 'lib1',
    };

    const audioState = {
        currentTrack: mockTrack,
        isPlaying: true,
        volume: 1.0,
        isMuted: false,
        shuffleMode: false,
        repeatMode: 'off',
        queue: [],
        originalQueue: [],
        pause: vi.fn(),
        resume: vi.fn(),
        next: vi.fn(),
        previous: vi.fn(),
        toggleMute: vi.fn(),
        toggleShuffle: vi.fn(),
        cycleRepeatMode: vi.fn(),
        setVolume: vi.fn(),
        closePlayer: vi.fn(),
    };

    beforeEach(() => {
        analyserRenders.mockClear();
        mockAudioStore.mockReturnValue(audioState);
        window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
        window.HTMLMediaElement.prototype.pause = vi.fn();
        window.HTMLMediaElement.prototype.load = vi.fn();
    });

    it('settles instead of re-rendering forever when the analyser reports readiness', () => {
        // Throws "Maximum update depth exceeded" if the refs churn per commit.
        expect(() => render(<PersistentPlayer />)).not.toThrow();

        expect(screen.getByText('Test Song')).toBeTruthy();
        // A handful of renders is normal (element attach, then isReady); hundreds
        // means the ref/state feedback loop is back.
        expect(analyserRenders.mock.calls.length).toBeLessThan(25);
    });

    it('hands both audio elements to the analyser', () => {
        render(<PersistentPlayer />);
        expect(document.querySelectorAll('audio').length).toBe(2);
    });
});
