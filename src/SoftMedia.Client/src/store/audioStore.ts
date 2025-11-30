import { create } from 'zustand';
import type { MediaItem } from '../types';

interface AudioState {
    currentTrack: MediaItem | null;
    isPlaying: boolean;
    queue: MediaItem[];
    volume: number;
    isMuted: boolean;

    // Actions
    playTrack: (track: MediaItem) => void;
    pause: () => void;
    resume: () => void;
    next: () => void;
    previous: () => void;
    addToQueue: (track: MediaItem) => void;
    removeFromQueue: (trackId: string) => void;
    setVolume: (volume: number) => void;
    toggleMute: () => void;
    clearQueue: () => void;
}

export const useAudioStore = create<AudioState>((set, get) => ({
    currentTrack: null,
    isPlaying: false,
    queue: [],
    volume: 1.0,
    isMuted: false,

    playTrack: (track) => set({ currentTrack: track, isPlaying: true }),

    pause: () => set({ isPlaying: false }),

    resume: () => {
        if (get().currentTrack) {
            set({ isPlaying: true });
        }
    },

    next: () => {
        const { queue } = get();
        if (queue.length === 0) return;

        const nextTrack = queue[0];
        const remainingQueue = queue.slice(1);

        set({ currentTrack: nextTrack, queue: remainingQueue, isPlaying: true });
    },

    previous: () => {
        // V1: Just restart current track or go to previous if we implemented history
        // For now, no-op or restart logic would be handled by the player component seeking to 0
    },

    addToQueue: (track) => set((state) => ({ queue: [...state.queue, track] })),

    removeFromQueue: (trackId) => set((state) => ({
        queue: state.queue.filter(t => t.id !== trackId)
    })),

    setVolume: (volume) => set({ volume }),

    toggleMute: () => set((state) => ({ isMuted: !state.isMuted })),

    clearQueue: () => set({ queue: [] }),
}));
