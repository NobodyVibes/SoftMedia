import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * ER-050 (v2): engine-agnostic TTS hook.
 *
 * The hook today drives the browser's `speechSynthesis` API, but its public
 * contract is deliberately engine-agnostic so a future server-side Piper
 * engine can slot in without BookReader changes:
 *   - callers pass an array of pre-chunked {@link TtsSegment}s to `speak`
 *   - the hook voices them in order and fires `onSegmentStart` / `onSegmentEnd`
 *     callbacks that the caller uses to drive extra-speech UI (karaoke
 *     highlights, analytics, progress persistence)
 *   - `onEnd` fires once when the entire segment list drains
 *
 * For Web Speech we create one `SpeechSynthesisUtterance` per segment and fire
 * the callbacks from `utterance.onstart` / `onend`. A Piper engine would POST
 * the concatenated text to a server, receive a single audio stream plus
 * per-segment timing marks, and fire the same callbacks from a scheduler.
 *
 * Chromium watchdog workaround: utterances longer than ~15 seconds are cut
 * short by an internal timer. Callers should keep each segment under ~200
 * characters — `chunkTextForTts` does this.
 */

/**
 * One speak-then-advance unit. `text` is what the engine actually voices;
 * `rawStart` / `rawEnd` are offsets into the source text that produced this
 * segment so callers can rebuild a DOM range for highlighting. Offsets are
 * opaque to the engine — they pass through unchanged.
 */
export interface TtsSegment {
    text: string;
    rawStart: number;
    rawEnd: number;
}

interface UseTtsOptions {
    voice: string | null;
    rate: number;
    /** Fires when a segment begins voicing. Use for karaoke highlight on. */
    onSegmentStart?: (index: number, segment: TtsSegment) => void;
    /** Fires when a segment finishes voicing (or is superseded). Karaoke off. */
    onSegmentEnd?: (index: number, segment: TtsSegment) => void;
    /** Fires once after the final segment's onSegmentEnd. */
    onEnd?: () => void;
}

export interface TtsControls {
    isSpeaking: boolean;
    isPaused: boolean;
    voices: SpeechSynthesisVoice[];
    supported: boolean;
    lastError: string | null;
    speak: (segments: TtsSegment[]) => void;
    stop: () => void;
    pause: () => void;
    resume: () => void;
    /**
     * Jump to a sibling segment in the current queue. `delta = +1` plays the
     * next sentence, `-1` replays the previous. Clamps inside the queue
     * bounds; if the user presses skip-forward on the last segment we fire
     * the normal end-of-queue `onEnd` so the caller can advance the page.
     */
    skip: (delta: number) => void;
}

/**
 * Split `text` into {@link TtsSegment}s whose `text` is short enough to finish
 * before Chromium's ~15s watchdog cuts the utterance. `rawStart` / `rawEnd`
 * reference offsets in the *input* string — critical for karaoke callers that
 * need to rebuild a DOM range from a char offset pair.
 *
 * Strategy:
 *   1. Find sentence-terminator boundaries in the raw text, keeping the
 *      terminator with the preceding sentence.
 *   2. Greedy-merge short sentences up to MAX_CHUNK.
 *   3. Hard-split any single sentence longer than MAX_CHUNK at a whitespace
 *      boundary (falling back to a hard cut if no whitespace is found).
 *
 * The segment's `text` is normalized (collapsed whitespace, trimmed) so the
 * engine hears natural prosody; the raw offsets remain in the unmodified
 * input space so callers can re-locate the segment in the DOM.
 */
export const MAX_CHUNK = 200;
export function chunkTextForTts(text: string): TtsSegment[] {
    if (text.length === 0) return [];

    const normalize = (s: string) => s.replace(/\s+/g, ' ').trim();

    // Find sentence-terminator positions in the RAW text. The regex matches
    // runs of [.!?]+ followed by whitespace or end-of-string; `index + match.length`
    // is the offset *after* the terminator, which is where the next sentence
    // begins.
    const boundaries: number[] = [];
    const re = /[.!?]+(\s+|$)/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
        boundaries.push(m.index + m[0].length);
    }
    if (boundaries.length === 0 || boundaries[boundaries.length - 1] < text.length) {
        boundaries.push(text.length);
    }

    // Materialize sentence-level segments (no merging yet).
    const sentences: TtsSegment[] = [];
    let cursor = 0;
    for (const b of boundaries) {
        const slice = text.slice(cursor, b);
        const clean = normalize(slice);
        if (clean.length > 0) sentences.push({ text: clean, rawStart: cursor, rawEnd: b });
        cursor = b;
    }

    // Greedy-merge into chunks <= MAX_CHUNK, hard-splitting sentences that
    // individually exceed the budget.
    const out: TtsSegment[] = [];
    let buf: TtsSegment | null = null;
    const flushBuf = () => { if (buf) { out.push(buf); buf = null; } };

    for (const s of sentences) {
        if (s.text.length > MAX_CHUNK) {
            flushBuf();
            // Walk the raw slice and split at whitespace boundaries.
            let offset = s.rawStart;
            while (offset < s.rawEnd) {
                const remaining = text.slice(offset, s.rawEnd);
                if (remaining.length <= MAX_CHUNK) {
                    const clean = normalize(remaining);
                    if (clean.length > 0) {
                        out.push({ text: clean, rawStart: offset, rawEnd: s.rawEnd });
                    }
                    offset = s.rawEnd;
                    break;
                }
                const softCut = remaining.lastIndexOf(' ', MAX_CHUNK);
                const cutAt = softCut > MAX_CHUNK / 2 ? softCut : MAX_CHUNK;
                const clean = normalize(remaining.slice(0, cutAt));
                if (clean.length > 0) {
                    out.push({ text: clean, rawStart: offset, rawEnd: offset + cutAt });
                }
                offset += cutAt;
                // Skip leading whitespace so the next segment doesn't start with a space.
                while (offset < s.rawEnd && /\s/.test(text[offset])) offset += 1;
            }
            continue;
        }

        if (!buf) {
            buf = { ...s };
        } else if (buf.text.length + 1 + s.text.length <= MAX_CHUNK) {
            buf = {
                text: `${buf.text} ${s.text}`,
                rawStart: buf.rawStart,
                rawEnd: s.rawEnd,
            };
        } else {
            out.push(buf);
            buf = { ...s };
        }
    }
    flushBuf();
    return out;
}

export function useTts({
    voice,
    rate,
    onSegmentStart,
    onSegmentEnd,
    onEnd,
}: UseTtsOptions): TtsControls {
    const supported = typeof window !== 'undefined'
        && typeof window.speechSynthesis !== 'undefined';

    const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([]);
    const [isSpeaking, setIsSpeaking] = useState(false);
    const [isPaused, setIsPaused] = useState(false);
    const [lastError, setLastError] = useState<string | null>(null);

    // Keep the latest callbacks / voice / rate in refs so utterance handlers
    // (created at speak-time) always see current values. Without this, a
    // rate change mid-queue would not take effect until the next speak call.
    const onSegmentStartRef = useRef(onSegmentStart);
    const onSegmentEndRef = useRef(onSegmentEnd);
    const onEndRef = useRef(onEnd);
    const voiceRef = useRef(voice);
    const voicesRef = useRef(voices);
    const rateRef = useRef(rate);
    useEffect(() => { onSegmentStartRef.current = onSegmentStart; }, [onSegmentStart]);
    useEffect(() => { onSegmentEndRef.current = onSegmentEnd; }, [onSegmentEnd]);
    useEffect(() => { onEndRef.current = onEnd; }, [onEnd]);
    useEffect(() => { voiceRef.current = voice; }, [voice]);
    useEffect(() => { voicesRef.current = voices; }, [voices]);
    useEffect(() => { rateRef.current = rate; }, [rate]);

    // Generation token: each `speak()` / `stop()` increments this. Every
    // utterance's callbacks capture their generation and bail if the current
    // one has moved on — protects against a late onend from a canceled
    // utterance accidentally driving the fresh queue forward.
    const generationRef = useRef(0);
    // Active queue: indexed list of segments still to voice + the running
    // index of what's currently speaking. `activeIndex` feeds the segment
    // callback with a stable index across speakOne recursion.
    const pendingRef = useRef<TtsSegment[]>([]);
    const activeIndexRef = useRef<number>(-1);
    // Snapshot of the full segment list currently being voiced. Used to pass
    // the original TtsSegment (offsets intact) to onSegment* callbacks.
    const activeSegmentsRef = useRef<TtsSegment[]>([]);

    useEffect(() => {
        if (!supported) return;
        const synth = window.speechSynthesis;
        const update = () => setVoices(synth.getVoices());
        update();
        synth.addEventListener('voiceschanged', update);
        return () => synth.removeEventListener('voiceschanged', update);
    }, [supported]);

    const didLogRef = useRef(false);
    useEffect(() => {
        if (didLogRef.current) return;
        if (!supported) {
            didLogRef.current = true;
            // eslint-disable-next-line no-console
            console.info('[TTS] speechSynthesis not supported in this environment');
            return;
        }
        if (voices.length === 0) return;
        didLogRef.current = true;
        const def = voices.find((v) => v.default);
        // eslint-disable-next-line no-console
        console.info(
            `[TTS] supported=true voices=${voices.length}`
            + (def ? ` default=${def.name} (${def.lang})` : ''),
        );
    }, [supported, voices]);

    useEffect(() => {
        if (!supported) return;
        return () => {
            try { window.speechSynthesis.cancel(); } catch { /* ignore */ }
        };
    }, [supported]);

    const speakOne = useCallback((segment: TtsSegment, index: number, generation: number) => {
        const synth = window.speechSynthesis;
        const utterance = new SpeechSynthesisUtterance(segment.text);
        const chosen = voiceRef.current
            ? voicesRef.current.find((v) => v.name === voiceRef.current)
            : null;
        if (chosen) utterance.voice = chosen;
        utterance.rate = rateRef.current;

        utterance.onstart = () => {
            if (generation !== generationRef.current) return;
            activeIndexRef.current = index;
            setIsSpeaking(true);
            setIsPaused(false);
            setLastError(null);
            onSegmentStartRef.current?.(index, segment);
        };
        utterance.onpause = () => {
            if (generation !== generationRef.current) return;
            setIsPaused(true);
        };
        utterance.onresume = () => {
            if (generation !== generationRef.current) return;
            setIsPaused(false);
        };
        utterance.onend = () => {
            if (generation !== generationRef.current) return;
            onSegmentEndRef.current?.(index, segment);
            const next = pendingRef.current.shift();
            if (next !== undefined) {
                speakOne(next, index + 1, generation);
                return;
            }
            activeIndexRef.current = -1;
            setIsSpeaking(false);
            setIsPaused(false);
            onEndRef.current?.();
        };
        utterance.onerror = (e) => {
            if (generation !== generationRef.current) return;
            const reason = typeof (e as SpeechSynthesisErrorEvent).error === 'string'
                ? (e as SpeechSynthesisErrorEvent).error
                : 'unknown';
            if (reason === 'canceled' || reason === 'interrupted') return;
            setIsSpeaking(false);
            setIsPaused(false);
            setLastError(reason);
        };

        synth.speak(utterance);
    }, []);

    const speak = useCallback((segments: TtsSegment[]) => {
        if (!supported) {
            setLastError('speechSynthesis is not available in this browser');
            return;
        }
        if (segments.length === 0) {
            setLastError('empty-text');
            return;
        }

        // Supersede any in-flight queue: bump generation, clear state, cancel.
        generationRef.current += 1;
        activeSegmentsRef.current = segments;
        pendingRef.current = segments.slice(1);
        activeIndexRef.current = -1;
        try { window.speechSynthesis.cancel(); } catch { /* ignore */ }
        speakOne(segments[0], 0, generationRef.current);
    }, [supported, speakOne]);

    const stop = useCallback(() => {
        if (!supported) return;
        // If a segment was mid-voice, fire its onSegmentEnd so callers
        // don't leave a stale highlight on the page.
        const i = activeIndexRef.current;
        if (i >= 0 && onSegmentEndRef.current) {
            const seg = activeSegmentsRef.current[i];
            if (seg) onSegmentEndRef.current(i, seg);
        }
        generationRef.current += 1;
        pendingRef.current = [];
        activeSegmentsRef.current = [];
        activeIndexRef.current = -1;
        try {
            window.speechSynthesis.cancel();
        } catch { /* ignore */ }
        setIsSpeaking(false);
        setIsPaused(false);
    }, [supported]);

    const pause = useCallback(() => {
        if (!supported || !isSpeaking) return;
        try { window.speechSynthesis.pause(); } catch { /* ignore */ }
    }, [supported, isSpeaking]);

    const resume = useCallback(() => {
        if (!supported) return;
        try { window.speechSynthesis.resume(); } catch { /* ignore */ }
    }, [supported]);

    const skip = useCallback((delta: number) => {
        if (!supported) return;
        const segments = activeSegmentsRef.current;
        if (segments.length === 0) return;
        const cur = activeIndexRef.current;
        if (cur < 0) return;
        const target = cur + delta;

        // Forward past the last segment → drain cleanly so the caller's onEnd
        // fires and the page turns. Doing this here keeps skip-forward-at-
        // end-of-page consistent with natural queue drain.
        if (target >= segments.length) {
            onSegmentEndRef.current?.(cur, segments[cur]);
            generationRef.current += 1;
            pendingRef.current = [];
            activeIndexRef.current = -1;
            try { window.speechSynthesis.cancel(); } catch { /* ignore */ }
            setIsSpeaking(false);
            setIsPaused(false);
            onEndRef.current?.();
            return;
        }

        // Backward before index 0 → replay the first segment from the top.
        const clamped = target < 0 ? 0 : target;
        // Clear the current segment's UI (highlight) before transitioning.
        onSegmentEndRef.current?.(cur, segments[cur]);
        generationRef.current += 1;
        pendingRef.current = segments.slice(clamped + 1);
        activeIndexRef.current = -1;
        try { window.speechSynthesis.cancel(); } catch { /* ignore */ }
        speakOne(segments[clamped], clamped, generationRef.current);
    }, [supported, speakOne]);

    return { isSpeaking, isPaused, voices, supported, lastError, speak, stop, pause, resume, skip };
}
