import { useEffect, useCallback, useState } from 'react';

/**
 * Web Audio tap that feeds the music visualizers.
 *
 * The graph lives at module scope rather than per hook instance because:
 *  - PersistentPlayer unmounts its <audio> pair whenever the player is closed
 *    (`currentTrack === null`), so the elements handed to this hook change over
 *    the life of the page — the graph has to be *re-wired*, not re-created.
 *  - Browsers cap concurrent AudioContexts (Chrome allows ~6), so minting one
 *    per element pair hard-fails after a few close/re-open cycles.
 *
 * Signal path: <audio> -> MediaElementAudioSourceNode -> analyser -> masterGain
 * -> destination. The analyser sits BEFORE masterGain so the visualizer still
 * reacts at low or muted output — which is why PersistentPlayer drives volume
 * through `setGlobalVolume` instead of element.volume while this hook is ready.
 */

const FFT_SIZE = 512;
// getByteFrequencyData fills min(frequencyBinCount, buffer.length) bins, so a
// 64-entry buffer against a 512-point FFT keeps the low 64 bins (~0-5.5kHz at
// 48kHz) — the range music actually occupies. Sizing the buffer to the full bin
// count would leave the upper two thirds of every bar permanently flat.
const FREQUENCY_BINS = 64;
const TIME_DOMAIN_SAMPLES = 256;

// Static buffers that persist across re-renders; renderers read them in place.
const frequencyData = new Uint8Array(FREQUENCY_BINS);
const timeDomainData = new Uint8Array(TIME_DOMAIN_SAMPLES);

let audioContext: AudioContext | null = null;
let analyser: AnalyserNode | null = null;
let masterGain: GainNode | null = null;

// createMediaElementSource() throws InvalidStateError when called twice for the
// same element on the same context, so source nodes are cached per element.
const sourceNodes = new WeakMap<HTMLAudioElement, MediaElementAudioSourceNode>();

// The element pair currently feeding the analyser.
let connectedPair: { a: HTMLAudioElement; b: HTMLAudioElement } | null = null;

interface AudioAnalyserResult {
    frequencyData: Uint8Array;
    timeDomainData: Uint8Array;
    isReady: boolean;
    updateData: () => void;
    setGlobalVolume: (volume: number) => void;
}

function ensureContext(): boolean {
    if (audioContext) return true;

    const AudioContextClass =
        window.AudioContext ||
        (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioContextClass) return false;

    try {
        const context = new AudioContextClass();

        const node = context.createAnalyser();
        node.fftSize = FFT_SIZE;
        node.smoothingTimeConstant = 0.75;

        const gain = context.createGain();
        gain.gain.value = 1.0;

        node.connect(gain);
        gain.connect(context.destination);

        audioContext = context;
        analyser = node;
        masterGain = gain;
        return true;
    } catch (error) {
        console.error('[AudioAnalyser] Failed to create AudioContext:', error);
        return false;
    }
}

/**
 * A context created outside a user gesture starts suspended, and because the
 * elements route through it that means silent playback *and* flat analyser
 * output. Cheap enough to poke on every frame and every gesture.
 */
function resumeContext(): void {
    if (audioContext?.state === 'suspended') {
        // Rejects until the page has been interacted with; the next gesture retries.
        audioContext.resume().catch(() => { /* no-op */ });
    }
}

function getSourceNode(element: HTMLAudioElement): MediaElementAudioSourceNode | null {
    if (!audioContext) return null;

    const cached = sourceNodes.get(element);
    if (cached) return cached;

    try {
        const node = audioContext.createMediaElementSource(element);
        sourceNodes.set(element, node);
        return node;
    } catch (error) {
        console.error('[AudioAnalyser] Failed to tap audio element:', error);
        return null;
    }
}

/** Point the analyser at `a`/`b`. Returns true once both are feeding it. */
function connectElements(a: HTMLAudioElement, b: HTMLAudioElement): boolean {
    if (connectedPair && connectedPair.a === a && connectedPair.b === b) return true;
    if (!ensureContext() || !analyser) return false;

    const sourceA = getSourceNode(a);
    const sourceB = getSourceNode(b);
    if (!sourceA || !sourceB) return false;

    // Detach the previous pair first: a source node keeps capturing its element
    // even after that element leaves the DOM, so leaving it attached would mix a
    // dead source into the analyser.
    if (connectedPair) {
        sourceNodes.get(connectedPair.a)?.disconnect();
        sourceNodes.get(connectedPair.b)?.disconnect();
    }

    // Both elements stay connected at unity: during a gapless crossfade the
    // outgoing and incoming tracks overlap, and the visualizer should follow the
    // mix the listener actually hears. Element .volume performs the crossfade.
    sourceA.connect(analyser);
    sourceB.connect(analyser);

    connectedPair = { a, b };
    return true;
}

/**
 * Hook that connects HTML5 Audio elements to Web Audio API for visualization.
 * Handles dual audio elements used for gapless playback.
 */
export function useAudioAnalyser(
    audioA: HTMLAudioElement | null,
    audioB: HTMLAudioElement | null
): AudioAnalyserResult {
    const [isReady, setIsReady] = useState(false);

    // The player closed (an element went null): report not-ready IMMEDIATELY —
    // during render — so callers fall back to element volume in the same pass
    // instead of driving a masterGain nothing is routed through for a frame.
    const [lastPair, setLastPair] = useState({ a: audioA, b: audioB });
    if (audioA !== lastPair.a || audioB !== lastPair.b) {
        setLastPair({ a: audioA, b: audioB });
        if (!audioA || !audioB) setIsReady(false);
    }

    useEffect(() => {
        if (!audioA || !audioB) {
            return;
        }

        // Re-wire on every element identity change. Checking only "does a graph
        // exist" is what broke the visualizers: after the player was closed and
        // re-opened, the analyser stayed bolted to the discarded <audio> pair, so
        // isReady was true, the canvas kept drawing, and every sample read silence.
        const attach = () => {
            const connectedOk = connectElements(audioA, audioB);
            setIsReady(connectedOk);
            if (connectedOk) resumeContext();
        };

        attach();

        audioA.addEventListener('play', attach);
        audioB.addEventListener('play', attach);
        document.addEventListener('pointerdown', resumeContext);
        document.addEventListener('keydown', resumeContext);

        return () => {
            audioA.removeEventListener('play', attach);
            audioB.removeEventListener('play', attach);
            document.removeEventListener('pointerdown', resumeContext);
            document.removeEventListener('keydown', resumeContext);
        };
    }, [audioA, audioB]);

    // Function to update data buffers
    const updateData = useCallback(() => {
        if (!analyser) return;
        resumeContext();
        analyser.getByteFrequencyData(frequencyData);
        analyser.getByteTimeDomainData(timeDomainData);
    }, []);

    // Global volume, applied after the analyser so muting never flatlines the bars.
    const setGlobalVolume = useCallback((volume: number) => {
        if (!audioContext || !masterGain) return;
        // Smooth transition to avoid clicks
        masterGain.gain.setTargetAtTime(volume, audioContext.currentTime, 0.05);
    }, []);

    return {
        frequencyData,
        timeDomainData,
        isReady,
        updateData,
        setGlobalVolume
    };
}
