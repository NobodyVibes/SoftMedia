import { useState, useCallback, useRef, useEffect } from 'react';

interface UseSequentialRevealOptions {
    /** Delay in ms between each successive reveal (default 60). */
    staggerMs?: number;
    /**
     * If the cursor has been stuck waiting for the same image for this many ms,
     * force-advance past it. Prevents off-screen items (which never fire onLoad
     * because intersection-observer skips them) from stalling the cascade in
     * large grids. Default 400ms.
     */
    stuckTimeoutMs?: number;
}

interface UseSequentialRevealResult {
    /** Returns true if the image at this index should be visible. */
    isRevealed: (index: number) => boolean;
    /** Call when image at `index` finishes loading. */
    onImageLoad: (index: number) => void;
    /** Call when image at `index` fails — treated as "ready" so the cascade doesn't stall. */
    onImageError: (index: number) => void;
    /** Reset everything (call on filter/nav change, etc.). */
    reset: () => void;
}

/**
 * Coordinates a left-to-right cascading reveal for a row of images.
 * The cursor advances from index 0 → count-1 in order.
 * If the next image isn't loaded yet, the cascade pauses and resumes when it loads
 * (or when the stuck-timeout fires, whichever comes first).
 *
 * Count growth (e.g., infinite-scroll pagination) does NOT reset the cascade —
 * existing revealed items stay revealed and new items cascade in from where
 * the cursor left off. Count shrinkage (filter/nav change) does reset.
 */
export default function useSequentialReveal(
    count: number,
    options?: UseSequentialRevealOptions,
): UseSequentialRevealResult {
    const staggerMs = options?.staggerMs ?? 30;
    // Generous last-resort fallback. The primary mechanism for handling
    // off-viewport items is LoadingImage's own off-viewport auto-signal,
    // which calls onLoad within ~120ms for any slot that never enters view.
    // This timeout only protects against truly stuck slots (e.g. network hang
    // on a visible image) without prematurely skipping in-viewport items that
    // are just taking a moment to decode.
    const stuckTimeoutMs = options?.stuckTimeoutMs ?? 2000;

    // Trigger re-render when cursor moves. Value is unused — revealedRef holds truth.
    const [, setRevealedCount] = useState(0);
    const revealedRef = useRef(0);
    const countRef = useRef(count);
    countRef.current = count;

    // Set of indices that have finished loading (or errored)
    const readySet = useRef(new Set<number>());
    const advanceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const stuckTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

    const clearStuckTimer = () => {
        if (stuckTimerRef.current !== null) {
            clearTimeout(stuckTimerRef.current);
            stuckTimerRef.current = null;
        }
    };

    const armStuckTimer = useCallback(() => {
        clearStuckTimer();
        const waitingIndex = revealedRef.current;
        if (waitingIndex >= countRef.current) return; // nothing to wait for

        stuckTimerRef.current = setTimeout(() => {
            stuckTimerRef.current = null;
            // If still waiting on the same index, treat it as ready and continue
            if (revealedRef.current === waitingIndex && !readySet.current.has(waitingIndex)) {
                readySet.current.add(waitingIndex);
                scheduleAdvance();
            }
        }, stuckTimeoutMs);
    }, [stuckTimeoutMs]);

    // Advance the cursor as far as possible, one step per stagger interval
    const scheduleAdvance = useCallback(() => {
        if (advanceTimerRef.current !== null) return; // already scheduled

        const step = () => {
            const next = revealedRef.current;
            if (next < countRef.current && readySet.current.has(next)) {
                revealedRef.current = next + 1;
                setRevealedCount(next + 1);
                clearStuckTimer();

                // Schedule the next step after a stagger delay
                if (next + 1 < countRef.current && readySet.current.has(next + 1)) {
                    advanceTimerRef.current = setTimeout(() => {
                        advanceTimerRef.current = null;
                        step();
                    }, staggerMs);
                } else {
                    advanceTimerRef.current = null;
                    // Waiting on next image — arm stuck-timer to eventually force past it
                    armStuckTimer();
                }
            } else {
                advanceTimerRef.current = null;
                armStuckTimer();
            }
        };

        // Run the first step synchronously — no initial delay. This lets the first
        // image reveal immediately when it loads, so the cascade starts instantly.
        // Subsequent steps still use the stagger delay for the wave effect.
        step();
    }, [staggerMs, armStuckTimer]);

    const markReady = useCallback((index: number) => {
        readySet.current.add(index);
        // If this is the index the cursor is waiting on, kick the cascade
        if (index === revealedRef.current) {
            scheduleAdvance();
        }
    }, [scheduleAdvance]);

    const onImageLoad = useCallback((index: number) => markReady(index), [markReady]);
    const onImageError = useCallback((index: number) => markReady(index), [markReady]);

    const isRevealed = useCallback((index: number) => index < revealedRef.current, []);

    const reset = useCallback(() => {
        if (advanceTimerRef.current !== null) {
            clearTimeout(advanceTimerRef.current);
            advanceTimerRef.current = null;
        }
        clearStuckTimer();
        readySet.current.clear();
        revealedRef.current = 0;
        setRevealedCount(0);
    }, []);

    // Smart auto-reset: only reset on count shrink (filter/nav change).
    // Count growth (infinite-scroll pagination) should preserve existing revealed
    // items and let new items cascade in from the cursor position.
    const prevCountRef = useRef(count);
    useEffect(() => {
        if (count < prevCountRef.current) {
            reset();
        } else if (count > prevCountRef.current && revealedRef.current < count) {
            // Count grew and we may be done waiting on something — re-arm stuck timer
            // in case new items need force-advancing.
            armStuckTimer();
        }
        prevCountRef.current = count;
    }, [count, reset, armStuckTimer]);

    // Cleanup on unmount
    useEffect(() => {
        return () => {
            if (advanceTimerRef.current !== null) clearTimeout(advanceTimerRef.current);
            clearStuckTimer();
        };
    }, []);

    return { isRevealed, onImageLoad, onImageError, reset };
}
