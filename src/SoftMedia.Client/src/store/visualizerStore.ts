import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type VisualizerType = 'bars' | 'waveform' | 'circular' | 'particles';

interface VisualizerState {
    isEnabled: boolean;
    isFullscreen: boolean;
    activeVisualizer: VisualizerType;

    // Actions
    toggle: () => void;
    setEnabled: (enabled: boolean) => void;
    setFullscreen: (fullscreen: boolean) => void;
    setActiveVisualizer: (visualizer: VisualizerType) => void;
}

export const useVisualizerStore = create<VisualizerState>()(
    persist(
        (set) => ({
            isEnabled: false,
            isFullscreen: false,
            activeVisualizer: 'bars',

            toggle: () => set((state) => ({ isEnabled: !state.isEnabled })),
            setEnabled: (enabled) => set({ isEnabled: enabled }),
            setFullscreen: (fullscreen) => set({ isFullscreen: fullscreen }),
            setActiveVisualizer: (visualizer) => set({ activeVisualizer: visualizer }),
        }),
        {
            name: 'visualizer-storage',
            // Only persist visualizer type preference, not enabled state or fullscreen
            partialize: (state) => ({
                activeVisualizer: state.activeVisualizer,
            }),
        }
    )
);
