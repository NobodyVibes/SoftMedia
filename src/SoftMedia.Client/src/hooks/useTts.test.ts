import { renderHook, act } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTts, chunkTextForTts, MAX_CHUNK, type TtsSegment } from './useTts';

/**
 * jsdom doesn't ship `window.speechSynthesis` or `SpeechSynthesisUtterance`,
 * so we install a minimal fake that mirrors the subset the hook touches.
 */

type FakeUtterance = {
    text: string;
    voice: SpeechSynthesisVoice | null;
    rate: number;
    onstart: (() => void) | null;
    onend: (() => void) | null;
    onerror: ((e: { error: string }) => void) | null;
    onpause: (() => void) | null;
    onresume: (() => void) | null;
};

let utterances: FakeUtterance[] = [];
let voicesChangedHandler: ((e: Event) => void) | null = null;
let availableVoices: SpeechSynthesisVoice[] = [];
const cancelSpy = vi.fn();
const speakSpy = vi.fn((u: FakeUtterance) => { utterances.push(u); });

function latestUtterance(): FakeUtterance | undefined {
    return utterances[utterances.length - 1];
}

function installSpeechSynthesisFake(initialVoices: SpeechSynthesisVoice[] = []) {
    availableVoices = initialVoices;
    utterances = [];
    voicesChangedHandler = null;
    cancelSpy.mockClear();
    speakSpy.mockClear();

    const synth = {
        cancel: cancelSpy,
        speak: speakSpy,
        pause: vi.fn(),
        resume: vi.fn(),
        getVoices: () => availableVoices,
        addEventListener: (type: string, handler: (e: Event) => void) => {
            if (type === 'voiceschanged') voicesChangedHandler = handler;
        },
        removeEventListener: (type: string) => {
            if (type === 'voiceschanged') voicesChangedHandler = null;
        },
    };

    (window as unknown as { speechSynthesis: unknown }).speechSynthesis = synth;
    (window as unknown as { SpeechSynthesisUtterance: unknown }).SpeechSynthesisUtterance =
        class {
            text: string;
            voice: SpeechSynthesisVoice | null = null;
            rate = 1;
            onstart: (() => void) | null = null;
            onend: (() => void) | null = null;
            onerror: ((e: { error: string }) => void) | null = null;
            onpause: (() => void) | null = null;
            onresume: (() => void) | null = null;
            constructor(text: string) { this.text = text; }
        };
}

function publishVoices(voices: SpeechSynthesisVoice[]) {
    availableVoices = voices;
    if (voicesChangedHandler) voicesChangedHandler(new Event('voiceschanged'));
}

function mkVoice(name: string, lang = 'en-US', isDefault = false): SpeechSynthesisVoice {
    return { name, lang, default: isDefault, localService: true, voiceURI: name } as SpeechSynthesisVoice;
}

// Helper: drain the engine by walking each queued utterance's onstart/onend
// in order, simulating a natural playback. Stops once the synthesizer has
// no new speak calls left to service.
function drainQueue() {
    // We can't know upfront how many utterances the hook will chain — each
    // onend may schedule the next speak. Iterate until stable.
    let cursor = 0;
    while (cursor < utterances.length) {
        const u = utterances[cursor];
        u.onstart?.();
        u.onend?.();
        cursor += 1;
    }
}

describe('chunkTextForTts', () => {
    it('returns an empty array for whitespace-only input', () => {
        expect(chunkTextForTts('')).toEqual([]);
        expect(chunkTextForTts('   \n\t  ')).toEqual([]);
    });

    it('returns a single segment for a single short sentence', () => {
        const segs = chunkTextForTts('Hello world.');
        expect(segs).toHaveLength(1);
        expect(segs[0].text).toBe('Hello world.');
        expect(segs[0].rawStart).toBe(0);
        expect(segs[0].rawEnd).toBe('Hello world.'.length);
    });

    it('groups short sentences up to MAX_CHUNK', () => {
        const text = 'One. Two. Three. Four. Five.';
        const segs = chunkTextForTts(text);
        expect(segs).toHaveLength(1);
        expect(segs[0].text).toBe('One. Two. Three. Four. Five.');
        expect(segs[0].rawStart).toBe(0);
        expect(segs[0].rawEnd).toBe(text.length);
    });

    it('never emits a segment whose text is longer than MAX_CHUNK', () => {
        const long = Array.from({ length: 50 }, (_, i) => `word${i}`).join(' ');
        const segs = chunkTextForTts(long);
        expect(segs.length).toBeGreaterThan(1);
        for (const s of segs) expect(s.text.length).toBeLessThanOrEqual(MAX_CHUNK);
    });

    it('preserves raw offsets across multi-sentence input', () => {
        const text = 'First sentence. Second sentence.';
        const segs = chunkTextForTts(text);
        // Merged into one since both short.
        expect(segs).toHaveLength(1);
        expect(segs[0].rawStart).toBe(0);
        expect(segs[0].rawEnd).toBe(text.length);
    });

    it('segment rawStart/rawEnd cover a slice whose normalized text matches segment.text', () => {
        // Force multiple segments by making sentences long enough that greedy
        // merge can't combine them.
        const sentence = 'x '.repeat(90).trim() + '.';
        const text = `${sentence} ${sentence}`;
        const segs = chunkTextForTts(text);
        expect(segs.length).toBeGreaterThan(1);
        for (const s of segs) {
            const rawSlice = text.slice(s.rawStart, s.rawEnd).replace(/\s+/g, ' ').trim();
            expect(rawSlice).toBe(s.text);
        }
    });
});

describe('useTts', () => {
    beforeEach(() => { installSpeechSynthesisFake(); });
    afterEach(() => {
        delete (window as unknown as { speechSynthesis?: unknown }).speechSynthesis;
        delete (window as unknown as { SpeechSynthesisUtterance?: unknown }).SpeechSynthesisUtterance;
    });

    function segs(text: string): TtsSegment[] {
        return chunkTextForTts(text);
    }

    it('reports supported when speechSynthesis is present', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        expect(result.current.supported).toBe(true);
    });

    it('picks up voices on voiceschanged', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        expect(result.current.voices).toHaveLength(0);
        act(() => { publishVoices([mkVoice('Alice'), mkVoice('Bob')]); });
        expect(result.current.voices).toHaveLength(2);
    });

    it('surfaces empty-segment-list as lastError and does not speak', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        act(() => { result.current.speak([]); });
        expect(speakSpy).not.toHaveBeenCalled();
        expect(result.current.lastError).toBe('empty-text');
    });

    it('clears lastError once the first segment starts', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        act(() => { result.current.speak([]); });
        expect(result.current.lastError).toBe('empty-text');
        act(() => { result.current.speak(segs('hello world.')); });
        act(() => { latestUtterance()?.onstart?.(); });
        expect(result.current.lastError).toBeNull();
        expect(result.current.isSpeaking).toBe(true);
    });

    it('captures utterance error reason into lastError', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        act(() => { result.current.speak(segs('hello world.')); });
        act(() => { latestUtterance()?.onerror?.({ error: 'synthesis-failed' }); });
        expect(result.current.lastError).toBe('synthesis-failed');
        expect(result.current.isSpeaking).toBe(false);
    });

    it('suppresses canceled/interrupted reasons', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        act(() => { result.current.speak(segs('hello world.')); });
        act(() => { latestUtterance()?.onstart?.(); });
        act(() => { latestUtterance()?.onerror?.({ error: 'canceled' }); });
        expect(result.current.lastError).toBeNull();
    });

    it('fires onSegmentStart / onSegmentEnd per segment with increasing index', () => {
        const starts: number[] = [];
        const ends: number[] = [];
        const { result } = renderHook(() =>
            useTts({
                voice: null,
                rate: 1,
                onSegmentStart: (i) => starts.push(i),
                onSegmentEnd: (i) => ends.push(i),
            }),
        );
        // Produce multiple segments by feeding a block that forces chunking.
        const long = Array.from({ length: 40 }, (_, i) => `Sentence ${i}.`).join(' ');
        const segments = chunkTextForTts(long);
        expect(segments.length).toBeGreaterThan(1);

        act(() => { result.current.speak(segments); });
        act(() => { drainQueue(); });

        expect(starts).toEqual(Array.from({ length: segments.length }, (_, i) => i));
        expect(ends).toEqual(starts);
    });

    it('fires onEnd exactly once, after the final onSegmentEnd', () => {
        const onEnd = vi.fn();
        const endIndices: number[] = [];
        const { result } = renderHook(() =>
            useTts({
                voice: null,
                rate: 1,
                onSegmentEnd: (i) => endIndices.push(i),
                onEnd,
            }),
        );
        const long = Array.from({ length: 40 }, (_, i) => `Sentence ${i}.`).join(' ');
        const segments = chunkTextForTts(long);
        act(() => { result.current.speak(segments); });
        act(() => { drainQueue(); });
        expect(onEnd).toHaveBeenCalledTimes(1);
        // onEnd fires after the last segment's onSegmentEnd.
        expect(endIndices[endIndices.length - 1]).toBe(segments.length - 1);
    });

    it('supersede: a new speak() call invalidates the old queue', () => {
        const onEnd = vi.fn();
        const { result } = renderHook(() => useTts({ voice: null, rate: 1, onEnd }));
        const longSegments = chunkTextForTts(
            Array.from({ length: 40 }, (_, i) => `Sentence ${i}.`).join(' '),
        );
        act(() => { result.current.speak(longSegments); });
        const firstUtterance = latestUtterance();

        // New speak before the old queue drains.
        act(() => { result.current.speak(segs('Fresh short sentence.')); });

        // Late onend from the old utterance must not drive onEnd.
        act(() => { firstUtterance?.onend?.(); });
        expect(onEnd).not.toHaveBeenCalled();

        // The new queue completes.
        act(() => { drainQueue(); });
        expect(onEnd).toHaveBeenCalledTimes(1);
    });

    it('stop() fires onSegmentEnd for the active segment so highlights clean up', () => {
        const onSegmentEnd = vi.fn();
        const { result } = renderHook(() =>
            useTts({ voice: null, rate: 1, onSegmentEnd }),
        );
        act(() => { result.current.speak(segs('hello world.')); });
        act(() => { latestUtterance()?.onstart?.(); });
        expect(onSegmentEnd).not.toHaveBeenCalled();

        act(() => { result.current.stop(); });
        expect(onSegmentEnd).toHaveBeenCalledWith(0, expect.objectContaining({ text: 'hello world.' }));
        expect(result.current.isSpeaking).toBe(false);
    });

    it('applies the selected voice by name and the given rate', () => {
        const alice = mkVoice('Alice');
        const bob = mkVoice('Bob');
        const { result } = renderHook(() => useTts({ voice: 'Bob', rate: 1.5 }));
        act(() => { publishVoices([alice, bob]); });
        act(() => { result.current.speak(segs('hello world.')); });
        expect(latestUtterance()?.voice?.name).toBe('Bob');
        expect(latestUtterance()?.rate).toBe(1.5);
    });
});

describe('useTts skip', () => {
    beforeEach(() => { installSpeechSynthesisFake(); });
    afterEach(() => {
        delete (window as unknown as { speechSynthesis?: unknown }).speechSynthesis;
        delete (window as unknown as { SpeechSynthesisUtterance?: unknown }).SpeechSynthesisUtterance;
    });

    // The chunker merges short sentences, so tests that need multiple
    // segments build a list of long sentences that can't be merged under
    // the MAX_CHUNK cap. 50 "Sentence N." entries comfortably exceed it.
    function multiSegmentText(): ReturnType<typeof chunkTextForTts> {
        const long = Array.from({ length: 50 }, (_, i) => `Sentence ${i}.`).join(' ');
        return chunkTextForTts(long);
    }

    it('skip(+1) cancels the current utterance and starts the next segment', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        const segments = multiSegmentText();
        expect(segments.length).toBeGreaterThan(1);
        act(() => { result.current.speak(segments); });
        act(() => { latestUtterance()?.onstart?.(); });

        const beforeCount = speakSpy.mock.calls.length;
        act(() => { result.current.skip(1); });

        expect(cancelSpy).toHaveBeenCalled();
        // A fresh utterance for the next segment was queued.
        expect(speakSpy.mock.calls.length).toBe(beforeCount + 1);
    });

    it('skip(-1) replays the previous segment', () => {
        const starts: number[] = [];
        const { result } = renderHook(() =>
            useTts({ voice: null, rate: 1, onSegmentStart: (i) => starts.push(i) }),
        );
        const segments = multiSegmentText();
        act(() => { result.current.speak(segments); });
        // Advance to segment 2 by draining the first two.
        act(() => { latestUtterance()?.onstart?.(); latestUtterance()?.onend?.(); });
        act(() => { latestUtterance()?.onstart?.(); latestUtterance()?.onend?.(); });
        act(() => { latestUtterance()?.onstart?.(); });
        // At this point we're on segment 2. skip(-1) should restart segment 1.
        act(() => { result.current.skip(-1); });
        act(() => { latestUtterance()?.onstart?.(); });
        expect(starts[starts.length - 1]).toBe(1);
    });

    it('skip(+1) at the last segment drains cleanly and fires onEnd', () => {
        const onEnd = vi.fn();
        const { result } = renderHook(() => useTts({ voice: null, rate: 1, onEnd }));
        const segments = multiSegmentText();
        act(() => { result.current.speak(segments); });
        // Advance through all segments up to and including the last one.
        for (let i = 0; i < segments.length; i += 1) {
            act(() => { latestUtterance()?.onstart?.(); });
            if (i < segments.length - 1) {
                act(() => { latestUtterance()?.onend?.(); });
            }
        }
        // Now on the final segment, mid-utterance. skip(+1) should end it.
        act(() => { result.current.skip(1); });
        expect(onEnd).toHaveBeenCalledTimes(1);
    });

    it('skip ignored when nothing is playing', () => {
        const { result } = renderHook(() => useTts({ voice: null, rate: 1 }));
        act(() => { result.current.skip(1); });
        expect(speakSpy).not.toHaveBeenCalled();
        expect(cancelSpy).not.toHaveBeenCalled();
    });
});
