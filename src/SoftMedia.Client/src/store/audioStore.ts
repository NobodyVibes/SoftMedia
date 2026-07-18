import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { MediaItem } from '../types';

type RepeatMode = 'off' | 'one' | 'all';

interface AudioState {
    currentTrack: MediaItem | null;
    isPlaying: boolean;
    queue: MediaItem[];
    originalQueue: MediaItem[];  // For un-shuffling
    history: MediaItem[];
    volume: number;
    isMuted: boolean;
    shuffleMode: boolean;
    repeatMode: RepeatMode;

    // Actions
    playTrack: (track: MediaItem) => void;
    pause: () => void;
    resume: () => void;
    next: () => void;
    previous: () => void;
    addToQueue: (track: MediaItem) => void;
    addToQueueNext: (track: MediaItem) => void;
    removeFromQueue: (trackId: string) => void;
    reorderQueue: (fromIndex: number, toIndex: number) => void;
    setVolume: (volume: number) => void;
    toggleMute: () => void;
    clearQueue: () => void;
    playPlaylist: (tracks: MediaItem[], startFrom?: MediaItem) => void;
    toggleShuffle: () => void;
    cycleRepeatMode: () => void;
    setRepeatMode: (mode: RepeatMode) => void;
    jumpToQueueIndex: (index: number) => void;
    closePlayer: () => void;
}

// Fisher-Yates shuffle
const shuffleArray = <T>(array: T[]): T[] => {
    const shuffled = [...array];
    for (let i = shuffled.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [shuffled[i], shuffled[j]] = [shuffled[j], shuffled[i]];
    }
    return shuffled;
};

export const useAudioStore = create<AudioState>()(
    persist(
        (set, get) => ({
            currentTrack: null,
            isPlaying: false,
            queue: [],
            originalQueue: [],
            history: [],
            volume: 1.0,
            isMuted: false,
            shuffleMode: false,
            repeatMode: 'off' as RepeatMode,

            playTrack: (track) => {
                const { currentTrack, history } = get();
                // Add current track to history before switching
                const newHistory = currentTrack
                    ? [currentTrack, ...history].slice(0, 50)  // Keep last 50 tracks
                    : history;
                // B-08: playing a single track REPLACES the playback context.
                // Leaving the old queue in place meant a search-played track that
                // ended silently resumed a stale album from earlier in the session
                // (and repeat-all restarted it). originalQueue becomes just this
                // track so repeat-all loops it, not the old album. Queue building
                // stays the job of addToQueue/playPlaylist.
                set({
                    currentTrack: track,
                    isPlaying: true,
                    history: newHistory,
                    queue: [],
                    originalQueue: [track]
                });
            },

            pause: () => set({ isPlaying: false }),

            resume: () => {
                if (get().currentTrack) {
                    set({ isPlaying: true });
                }
            },

            next: () => {
                const { queue, currentTrack, history, repeatMode, originalQueue, shuffleMode } = get();

                // Add current to history
                const newHistory = currentTrack
                    ? [currentTrack, ...history].slice(0, 50)
                    : history;

                // Repeat One: Just restart (handled by player, but we can also handle here)
                if (repeatMode === 'one' && currentTrack) {
                    // Let the player handle restart by not changing track
                    set({ isPlaying: true });
                    return;
                }

                if (queue.length === 0) {
                    // Repeat All: Restart the playlist
                    if (repeatMode === 'all' && originalQueue.length > 0) {
                        const newQueue = shuffleMode
                            ? shuffleArray(originalQueue)
                            : [...originalQueue];
                        const nextTrack = newQueue[0];
                        set({
                            currentTrack: nextTrack,
                            queue: newQueue.slice(1),
                            history: newHistory,
                            isPlaying: true
                        });
                        return;
                    }
                    // No more tracks and no repeat
                    set({ isPlaying: false, history: newHistory });
                    return;
                }

                const nextTrack = queue[0];
                const remainingQueue = queue.slice(1);

                set({
                    currentTrack: nextTrack,
                    queue: remainingQueue,
                    history: newHistory,
                    isPlaying: true
                });
            },

            previous: () => {
                const { history, currentTrack, queue } = get();

                if (history.length === 0) {
                    // No history, maybe restart current track (player handles this)
                    return;
                }

                // Move current track to front of queue
                const newQueue = currentTrack ? [currentTrack, ...queue] : queue;
                const previousTrack = history[0];
                const newHistory = history.slice(1);

                set({
                    currentTrack: previousTrack,
                    queue: newQueue,
                    history: newHistory,
                    isPlaying: true
                });
            },

            addToQueue: (track) => set((state) => ({
                queue: [...state.queue, track],
                originalQueue: [...state.originalQueue, track]
            })),

            addToQueueNext: (track) => set((state) => ({
                queue: [track, ...state.queue],
                originalQueue: [track, ...state.originalQueue]
            })),

            removeFromQueue: (trackId) => set((state) => ({
                queue: state.queue.filter(t => t.id !== trackId),
                originalQueue: state.originalQueue.filter(t => t.id !== trackId)
            })),

            reorderQueue: (fromIndex, toIndex) => set((state) => {
                const newQueue = [...state.queue];
                const [moved] = newQueue.splice(fromIndex, 1);
                newQueue.splice(toIndex, 0, moved);
                return { queue: newQueue };
            }),

            setVolume: (volume) => set({ volume }),

            toggleMute: () => set((state) => ({ isMuted: !state.isMuted })),

            clearQueue: () => set({ queue: [], originalQueue: [], history: [] }),

            playPlaylist: (tracks, startFrom) => {
                if (!tracks || tracks.length === 0) return;

                const { shuffleMode, currentTrack, history } = get();

                let startIndex = 0;
                if (startFrom) {
                    startIndex = tracks.findIndex(t => t.id === startFrom.id);
                    if (startIndex === -1) startIndex = 0;
                }

                // Add current track to history
                const newHistory = currentTrack
                    ? [currentTrack, ...history].slice(0, 50)
                    : history;

                const currentTrackNew = tracks[startIndex];

                // Keep the FULL original queue for un-shuffling or repeating, don't filter out the started track
                const originalQueue = [...tracks];

                // The active queue is everything AFTER the start track. When the queue
                // drains, `next()` reloads from `originalQueue` if repeat='all', which
                // produces correct album-loop behavior (loop after the LAST track of the
                // LAST disc, not after the current track).
                let queue = tracks.slice(startIndex + 1);

                // Shuffle if needed
                if (shuffleMode) {
                    queue = shuffleArray(queue);
                }

                set({
                    currentTrack: currentTrackNew,
                    queue,
                    originalQueue, // Save FULL list
                    history: newHistory,
                    isPlaying: true
                });
            },

            toggleShuffle: () => {
                const { shuffleMode, queue, originalQueue, currentTrack } = get();

                if (shuffleMode) {
                    // Turn off shuffle - restore original order of remaining tracks
                    // We need to find where we are in the original queue
                    let newQueue = [...originalQueue];

                    if (currentTrack) {
                        const currentIndex = originalQueue.findIndex(t => t.id === currentTrack.id);
                        if (currentIndex !== -1) {
                            // Queue is everything AFTER the current track
                            newQueue = originalQueue.slice(currentIndex + 1);
                        } else {
                            // Current track not in original queue (maybe added later?), just keep full original?
                            // Or maybe we just keep the current queue but sorted?
                            // Safest: if track not found, just use original queue.
                        }
                    }

                    set({
                        shuffleMode: false,
                        queue: newQueue
                    });
                } else {
                    // Turn on shuffle
                    set({
                        shuffleMode: true,
                        queue: shuffleArray(queue)
                    });
                }
            },

            cycleRepeatMode: () => {
                const { repeatMode } = get();
                const modes: RepeatMode[] = ['off', 'all', 'one'];
                const currentIndex = modes.indexOf(repeatMode);
                const nextMode = modes[(currentIndex + 1) % modes.length];
                set({ repeatMode: nextMode });
            },

            setRepeatMode: (mode) => set({ repeatMode: mode }),

            jumpToQueueIndex: (index) => {
                const { queue, currentTrack, history } = get();
                if (index < 0 || index >= queue.length) return;

                // Add current and skipped tracks to history
                const skipped = queue.slice(0, index);
                const newHistory = currentTrack
                    ? [currentTrack, ...skipped, ...history].slice(0, 50)
                    : [...skipped, ...history].slice(0, 50);

                const newTrack = queue[index];
                const newQueue = queue.slice(index + 1);

                set({
                    currentTrack: newTrack,
                    queue: newQueue, // This reduces the queue. If repeat is on, 'next' will handle reloading from originalQueue
                    history: newHistory,
                    isPlaying: true
                });
            },

            closePlayer: () => {
                set({
                    currentTrack: null,
                    isPlaying: false,
                    // We can optionally clear queue or keep it. 
                    // Usually closing player means "stop everything".
                    // But keeping queue/history in background might be nice if they accidentally closed it?
                    // For now, adhering to "refresh page" equivalent behavior which clears session state usually.
                    // But the persistence says we only persist preferences. 
                    // So clearing currentTrack hides the UI.
                });
            }
        }),
        {
            name: 'audio-player-storage',
            // Only persist preferences, not playback state
            partialize: (state) => ({
                volume: state.volume,
                isMuted: state.isMuted,
                shuffleMode: state.shuffleMode,
                repeatMode: state.repeatMode
            })
        }
    )
);
