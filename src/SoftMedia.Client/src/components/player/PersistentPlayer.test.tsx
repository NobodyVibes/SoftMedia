import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PersistentPlayer } from './PersistentPlayer';
import type { MediaItem } from '../../types';

// Mock the stores and hooks
const mockAudioStore = vi.fn();
const mockVisualizerStore = vi.fn();
const mockAudioAnalyser = vi.fn();

vi.mock('../../store/audioStore', () => ({
    useAudioStore: () => mockAudioStore(),
}));

vi.mock('../../store/visualizerStore', () => ({
    useVisualizerStore: () => mockVisualizerStore(),
}));

vi.mock('../../hooks/useAudioAnalyser', () => ({
    useAudioAnalyser: () => mockAudioAnalyser(),
}));

// Mock child components that might need complex context or browser APIs
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

// Mock api service constants (+ default client for R-WI-013 progress beats)
vi.mock('../../services/api', () => ({
    API_URL: 'http://localhost:5000/api',
    default: { post: vi.fn().mockResolvedValue({}) },
}));

describe('PersistentPlayer', () => {
    const mockTrack: MediaItem = {
        id: '1',
        title: 'Test Song',
        sortTitle: 'Test Song',
        dateAdded: '2023-01-01',
        type: 'Audio',
        path: '/music/test.mp3',
        metadata: {
            artist: 'Test Artist',
            album: 'Test Album',
            duration: 180,
        },
        libraryId: 'lib1',
    };

    const defaultAudioState = {
        currentTrack: null,
        isPlaying: false,
        volume: 1.0,
        isMuted: false,
        shuffleMode: false,
        repeatMode: 'off',
        queue: [],
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

    const defaultVisualizerState = {
        isEnabled: false,
        toggle: vi.fn(),
    };

    const defaultAnalyserState = {
        frequencyData: new Uint8Array(),
        timeDomainData: new Uint8Array(),
        isReady: false,
        updateData: vi.fn(),
        setGlobalVolume: vi.fn(),
    };

    beforeEach(() => {
        mockAudioStore.mockReturnValue(defaultAudioState);
        mockVisualizerStore.mockReturnValue(defaultVisualizerState);
        mockAudioAnalyser.mockReturnValue(defaultAnalyserState);

        // Mock HTMLMediaElement methods that JSDOM doesn't implement
        window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
        window.HTMLMediaElement.prototype.pause = vi.fn();
        window.HTMLMediaElement.prototype.load = vi.fn();
    });

    it('renders nothing when no track is playing', () => {
        render(<PersistentPlayer />);
        expect(screen.queryByText('Test Song')).toBeNull();
    });

    it('renders player when track is present', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
        });

        render(<PersistentPlayer />);

        expect(screen.getByText('Test Song')).toBeInTheDocument();
        expect(screen.getByText('Test Artist')).toBeInTheDocument();
    });

    it('toggles play/pause', () => {
        const resumeMock = vi.fn();
        const pauseMock = vi.fn();

        // Case 1: Paused -> Play
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
            isPlaying: false,
            resume: resumeMock,
            pause: pauseMock,
        });

        render(<PersistentPlayer />);

        // Find play button (it shows Play icon when isPlaying is false)
        // Note: The mocked Lucide icons usually don't have text, but we can look for the button containing them or aria-label if we added one. 
        // In the code: <button onClick={isPlaying ? pause : resume}>...<Play />...</button>
        // We can find by role button. The big play/pause button is usually distinctive.
        // A better way is to rely on the button structure or title if available. Unfortunately the code doesn't have title on Play/Pause.
        // Let's deduce it's the one that calls resume.
        // Actually, let's update the code to have titles or aria-labels for better accessibility and testing later! 
        // For now, let's find the button by its distinctive class or content.
        // The play button has class "w-16 h-16 rounded-full..."

        // Alternatively, finding by icon SVG name if rendered by Lucide mock (which isn't mocked yet, so it renders SVG).
        // Since we didn't mock Lucide, the svgs are there.
        // Let's assume the button logic works. We can find the button that triggers the action.

        // Hack: The play button is the only one without a title attribute in the main controls?
        // Wait, the previous/next buttons don't have titles in the snippet I saw? 
        // Re-reading snippet:
        // Previous: title="Previous (Left Arrow)" - Wait in snippet it is `title="Shuffle (Shift+S)"` for shuffle...
        // Previous doesn't have title in the snippet: `<button onClick={handlePrevious} ...> <SkipBack ... /> </button>`

        // We might need to select by test-id or index.
        // Let's add test IDs? No, let's just use querySelector for now or verifying SVG presence.
        // Or finding by 'button' role and filtering.

        // Simpler approach: finding by role 'button' and trying to click the one that looks like Play.
        // But since we can't easily distinguish, maybe we can assume the center button is Play.

        // Let's rely on the fact that we mocked `resume` and `pause`.
        // Let's find all buttons and click them? No that's bad.

        // Let's modify the component in a future step to add aria-labels. 
        // FOR NOW: Let's assume we can try to find by the SVG content if JS DOM renders it.
        // Lucide icons render <svg ... class="lucide lucide-play" ...>

        // Trying to find the button containing the Play icon.
        // Use container.querySelector('.lucide-play').closest('button')

    });

    it('calls active playback controls', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
            isPlaying: false,
        });

        const { container } = render(<PersistentPlayer />);

        // Find Play button
        const playIcon = container.querySelector('.lucide-play');
        const playBtn = playIcon?.closest('button');
        expect(playBtn).toBeTruthy();

        if (playBtn) {
            fireEvent.click(playBtn);
            expect(defaultAudioState.resume).toHaveBeenCalled();
        }
    });

    it('calls pause when playing', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
            isPlaying: true, // Playing
        });

        const { container } = render(<PersistentPlayer />);

        const pauseIcon = container.querySelector('.lucide-pause');
        const pauseBtn = pauseIcon?.closest('button');
        expect(pauseBtn).toBeTruthy();

        if (pauseBtn) {
            fireEvent.click(pauseBtn);
            // In the component: onClick={isPlaying ? pause : resume}
            // but `pause` comes from `useAudioStore`.
            // The defaultAudioState.pause is a jest fn (vi.fn)
            expect(defaultAudioState.pause).toHaveBeenCalled();
        }
    });

    it('toggles shuffle', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
        });

        render(<PersistentPlayer />);

        const shuffleBtn = screen.getByTitle(/Shuffle/i);
        fireEvent.click(shuffleBtn);
        expect(defaultAudioState.toggleShuffle).toHaveBeenCalled();
    });

    it('toggles mute', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
        });

        const { container } = render(<PersistentPlayer />);

        // Initial: Not muted -> Volume2 icon
        const volumeIcon = container.querySelector('.lucide-volume-2');
        const muteBtn = volumeIcon?.closest('button');

        expect(muteBtn).toBeTruthy();
        if (muteBtn) {
            fireEvent.click(muteBtn);
            expect(defaultAudioState.toggleMute).toHaveBeenCalled();
        }
    });

    it('displays queue when toggle clicked', () => {
        mockAudioStore.mockReturnValue({
            ...defaultAudioState,
            currentTrack: mockTrack,
            queue: [mockTrack]
        });

        render(<PersistentPlayer />);

        const queueBtn = screen.getByTitle('Queue');
        fireEvent.click(queueBtn);

        expect(screen.getByTestId('queue-list')).toBeInTheDocument();
    });

});
