import { useEffect, useCallback, useState } from 'react';

// Module-level singleton to survive React Strict Mode double-mounts
interface AudioGraph {
    context: AudioContext;
    analyser: AnalyserNode;
    masterGain: GainNode;
    gainA: GainNode;
    gainB: GainNode;
    sourceA: MediaElementAudioSourceNode;
    sourceB: MediaElementAudioSourceNode;
    elementA: HTMLAudioElement;
    elementB: HTMLAudioElement;
}

let globalAudioGraph: AudioGraph | null = null;

// Static buffers that persist across re-renders
const frequencyData = new Uint8Array(64);
const timeDomainData = new Uint8Array(64);

interface AudioAnalyserResult {
    frequencyData: Uint8Array;
    timeDomainData: Uint8Array;
    isReady: boolean;
    updateData: () => void;
    setGlobalVolume: (volume: number) => void;
}

/**
 * Hook that connects HTML5 Audio elements to Web Audio API for visualization.
 * Handles dual audio elements used for gapless playback.
 */
export function useAudioAnalyser(
    audioA: HTMLAudioElement | null,
    audioB: HTMLAudioElement | null,
    activePlayer: 0 | 1
): AudioAnalyserResult {
    const [isReady, setIsReady] = useState(false);

    // Initialize Audio Context and connect elements
    const initializeAudioContext = useCallback(() => {
        if (!audioA || !audioB) return;

        // Return existing graph if valid for current elements
        if (globalAudioGraph) {
            if (globalAudioGraph.elementA === audioA && globalAudioGraph.elementB === audioB) {
                if (globalAudioGraph.context.state === 'suspended') {
                    globalAudioGraph.context.resume();
                }
                setIsReady(true);
                return;
            } else {
                // Elements changed (should be rare in persistent player), close old context
                // globalAudioGraph.context.close(); // Optional, depending on browser limit
                globalAudioGraph = null;
            }
        }

        try {
            // Create AudioContext with Safari fallback
            const AudioContextClass = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
            const audioContext = new AudioContextClass();

            // Create analyser node
            const analyser = audioContext.createAnalyser();
            analyser.fftSize = 128; // 64 frequency bins
            analyser.smoothingTimeConstant = 0.8;

            // Create gain nodes for sources (crossfading)
            const gainA = audioContext.createGain();
            const gainB = audioContext.createGain();

            // Create Master Gain for global volume
            const masterGain = audioContext.createGain();
            masterGain.gain.value = 1.0; // Default to full volume

            // Create media element sources
            // Note: This throws if called twice on same element in same context,
            // but we're creating a NEW context here, so it's safe.
            const sourceA = audioContext.createMediaElementSource(audioA);
            const sourceB = audioContext.createMediaElementSource(audioB);

            // Connect sources through channel gains to analyser
            // Flow: Source -> ChannelGain -> Analyser -> MasterGain -> Destination
            sourceA.connect(gainA);
            sourceB.connect(gainB);
            gainA.connect(analyser);
            gainB.connect(analyser);

            // Connect analyser to MasterGain, then to destination
            analyser.connect(masterGain);
            masterGain.connect(audioContext.destination);

            // Save to singleton
            globalAudioGraph = {
                context: audioContext,
                analyser,
                masterGain,
                gainA,
                gainB,
                sourceA,
                sourceB,
                elementA: audioA,
                elementB: audioB
            };

            // Set initial gain based on active player
            gainA.gain.value = activePlayer === 0 ? 1 : 0;
            gainB.gain.value = activePlayer === 1 ? 1 : 0;

            setIsReady(true);
            console.log('[AudioAnalyser] Initialized with singleton graph');
        } catch (error) {
            console.error('[AudioAnalyser] Failed to initialize:', error);
        }
    }, [audioA, audioB, activePlayer]);

    // Initialize immediately when audio elements are available
    useEffect(() => {
        if (!audioA || !audioB) return;

        // If audio is already playing or has played, init immediately
        if (!globalAudioGraph) {
            // Try to init - user gesture may be required
            initializeAudioContext();
        } else {
            setIsReady(true);
        }

        // Also listen for play events as fallback
        const handlePlay = () => {
            if (!globalAudioGraph) {
                initializeAudioContext();
            } else if (globalAudioGraph.context.state === 'suspended') {
                globalAudioGraph.context.resume();
            }
        };

        audioA.addEventListener('play', handlePlay);
        audioB.addEventListener('play', handlePlay);

        // Also init on any user interaction as a fallback
        const handleUserInteraction = () => {
            if (!globalAudioGraph) {
                initializeAudioContext();
            } else if (globalAudioGraph.context.state === 'suspended') {
                globalAudioGraph.context.resume();
            }
        };

        document.addEventListener('click', handleUserInteraction, { once: true });

        return () => {
            audioA.removeEventListener('play', handlePlay);
            audioB.removeEventListener('play', handlePlay);
            document.removeEventListener('click', handleUserInteraction);
        };
    }, [audioA, audioB, initializeAudioContext]);

    // Update gain nodes based on active player
    useEffect(() => {
        if (!globalAudioGraph) return;

        const { gainA, gainB } = globalAudioGraph;
        gainA.gain.value = activePlayer === 0 ? 1 : 0;
        gainB.gain.value = activePlayer === 1 ? 1 : 0;
    }, [activePlayer, isReady]);

    // Cleanup on unmount - do NOT close context to support persistence
    useEffect(() => {
        return () => {
            // No cleanup needed for singleton
        };
    }, []);

    // Function to update data buffers
    const updateData = useCallback(() => {
        if (globalAudioGraph && isReady) {
            globalAudioGraph.analyser.getByteFrequencyData(frequencyData);
            globalAudioGraph.analyser.getByteTimeDomainData(timeDomainData);
        }
    }, [isReady]);

    // New function to control global volume
    const setGlobalVolume = useCallback((volume: number) => {
        if (globalAudioGraph) {
            // Smooth transition to avoid clicks
            const currentTime = globalAudioGraph.context.currentTime || 0;
            globalAudioGraph.masterGain.gain.setTargetAtTime(volume, currentTime, 0.05);
        }
    }, []);

    return {
        frequencyData,
        timeDomainData,
        isReady,
        updateData,
        setGlobalVolume
    };
}
