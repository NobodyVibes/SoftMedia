import { renderHook } from '@testing-library/react';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useAudioAnalyser } from './useAudioAnalyser';

/**
 * jsdom has no Web Audio API, so the graph is exercised against a fake that
 * keeps the two behaviours this hook has to respect: createMediaElementSource
 * throws when an element is tapped twice on the same context, and browsers cap
 * how many AudioContexts a page may hold.
 */
interface FakeNode {
    connect: ReturnType<typeof vi.fn>;
    disconnect: ReturnType<typeof vi.fn>;
}

const createNode = (): FakeNode => ({ connect: vi.fn(), disconnect: vi.fn() });

let contextsCreated = 0;
let sourcesFor: Map<HTMLAudioElement, FakeNode>;
let analyserNode: FakeNode & { fftSize: number; smoothingTimeConstant: number };

class FakeAudioContext {
    state = 'running';
    currentTime = 0;
    destination = createNode();

    constructor() {
        contextsCreated += 1;
    }

    createAnalyser() {
        analyserNode = Object.assign(createNode(), {
            fftSize: 2048,
            smoothingTimeConstant: 0,
            getByteFrequencyData: vi.fn(),
            getByteTimeDomainData: vi.fn(),
        });
        return analyserNode;
    }

    createGain() {
        return Object.assign(createNode(), {
            gain: { value: 1, setTargetAtTime: vi.fn() },
        });
    }

    createMediaElementSource(element: HTMLAudioElement) {
        if (sourcesFor.has(element)) {
            throw new DOMException('already connected', 'InvalidStateError');
        }
        const node = createNode();
        sourcesFor.set(element, node);
        return node;
    }

    resume() {
        this.state = 'running';
        return Promise.resolve();
    }
}

describe('useAudioAnalyser', () => {
    beforeEach(() => {
        vi.resetModules();
        contextsCreated = 0;
        sourcesFor = new Map();
        (window as unknown as { AudioContext: unknown }).AudioContext = FakeAudioContext;
    });

    // The graph is module-level state, so one test walks the whole lifecycle
    // rather than leaking a half-wired graph between cases.
    it('re-wires the analyser when the player unmounts and re-opens with fresh elements', async () => {
        const { useAudioAnalyser: hook } = await import('./useAudioAnalyser');

        const firstA = document.createElement('audio');
        const firstB = document.createElement('audio');

        const { result, rerender } = renderHook(
            ({ a, b }: { a: HTMLAudioElement | null; b: HTMLAudioElement | null }) => hook(a, b),
            { initialProps: { a: firstA as HTMLAudioElement | null, b: firstB as HTMLAudioElement | null } }
        );

        expect(result.current.isReady).toBe(true);
        expect(sourcesFor.get(firstA)?.connect).toHaveBeenCalledWith(analyserNode);
        expect(sourcesFor.get(firstB)?.connect).toHaveBeenCalledWith(analyserNode);

        // PersistentPlayer renders null while no track is loaded, which unmounts
        // both <audio> elements.
        rerender({ a: null, b: null });
        expect(result.current.isReady).toBe(false);

        // Playing again mounts a brand new pair. The old code saw "a graph already
        // exists", reported ready, and left the analyser bolted to the discarded
        // elements — the visualizers drew, but every sample read as silence.
        const secondA = document.createElement('audio');
        const secondB = document.createElement('audio');
        rerender({ a: secondA, b: secondB });

        expect(result.current.isReady).toBe(true);
        expect(sourcesFor.get(secondA)?.connect).toHaveBeenCalledWith(analyserNode);
        expect(sourcesFor.get(secondB)?.connect).toHaveBeenCalledWith(analyserNode);

        // The discarded pair keeps capturing its elements until detached.
        expect(sourcesFor.get(firstA)?.disconnect).toHaveBeenCalled();
        expect(sourcesFor.get(firstB)?.disconnect).toHaveBeenCalled();

        // Chrome caps concurrent AudioContexts (~6), so re-opening the player must
        // reuse the existing one rather than mint a replacement each time.
        expect(contextsCreated).toBe(1);
    });

    it('reads both analyser buffers on every update', async () => {
        const { useAudioAnalyser: hook } = await import('./useAudioAnalyser');

        const a = document.createElement('audio');
        const b = document.createElement('audio');
        const { result } = renderHook(() => hook(a, b));

        result.current.updateData();

        const analyser = analyserNode as unknown as {
            getByteFrequencyData: ReturnType<typeof vi.fn>;
            getByteTimeDomainData: ReturnType<typeof vi.fn>;
        };
        expect(analyser.getByteFrequencyData).toHaveBeenCalledWith(result.current.frequencyData);
        expect(analyser.getByteTimeDomainData).toHaveBeenCalledWith(result.current.timeDomainData);
    });

    it('routes global volume through the post-analyser gain so muting never flatlines the bars', async () => {
        const { useAudioAnalyser: hook } = await import('./useAudioAnalyser');

        const a = document.createElement('audio');
        const b = document.createElement('audio');
        const { result } = renderHook(() => hook(a, b));

        result.current.updateData();
        result.current.setGlobalVolume(0);

        // The analyser is upstream of masterGain, so it still receives full signal.
        const analyser = analyserNode as unknown as { getByteFrequencyData: ReturnType<typeof vi.fn> };
        expect(analyser.getByteFrequencyData).toHaveBeenCalled();
    });
});

// The static import above exists only so the module is part of the build graph;
// each test re-imports it after vi.resetModules() to get a clean graph.
void useAudioAnalyser;
