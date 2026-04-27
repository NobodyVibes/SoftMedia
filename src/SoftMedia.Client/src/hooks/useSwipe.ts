import { useEffect, type RefObject } from 'react';

/**
 * Options for {@link useSwipe}. `threshold` is the minimum horizontal distance
 * (in pixels) that separates a tap from a swipe; `maxVertical` caps how much
 * vertical drift a swipe may carry before we stop treating it as horizontal —
 * this is what lets the user vertical-scroll inside a tall PDF page without
 * accidentally turning the page.
 */
interface SwipeOptions {
    onSwipeLeft?: () => void;
    onSwipeRight?: () => void;
    threshold?: number;
    maxVertical?: number;
}

/**
 * Attach horizontal-swipe handling to a DOM element via the Pointer Events
 * API. Only the primary pointer in a single interaction is tracked, which
 * prevents multi-touch gestures (pinch-zoom, two-finger scroll) from being
 * misread as a swipe. Swipes starting on text inputs or any descendant with
 * `data-no-swipe` are ignored so the reader's page-jump input and future TOC
 * controls don't eat the gesture.
 */
export function useSwipe<T extends HTMLElement>(
    ref: RefObject<T | null>,
    {
        onSwipeLeft,
        onSwipeRight,
        threshold = 50,
        maxVertical = 30,
    }: SwipeOptions,
): void {
    useEffect(() => {
        const el = ref.current;
        if (!el) return;

        let startX = 0;
        let startY = 0;
        let activePointerId: number | null = null;
        let suppressed = false;

        const isSuppressedTarget = (target: EventTarget | null) => {
            if (!(target instanceof HTMLElement)) return false;
            if (target.closest('input, textarea, [contenteditable="true"]')) return true;
            if (target.closest('[data-no-swipe]')) return true;
            return false;
        };

        const handleDown = (e: PointerEvent) => {
            // Accept only the first pointer of a new interaction. If a second
            // touch arrives mid-gesture, suppress the whole thing (pinch/zoom).
            if (activePointerId !== null) {
                suppressed = true;
                return;
            }
            activePointerId = e.pointerId;
            suppressed = isSuppressedTarget(e.target);
            startX = e.clientX;
            startY = e.clientY;
        };

        const handleUp = (e: PointerEvent) => {
            if (e.pointerId !== activePointerId) return;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;

            activePointerId = null;

            if (suppressed) {
                suppressed = false;
                return;
            }
            if (Math.abs(dy) > maxVertical) return;
            if (Math.abs(dx) < threshold) return;

            if (dx < 0) onSwipeLeft?.();
            else onSwipeRight?.();
        };

        const handleCancel = (e: PointerEvent) => {
            if (e.pointerId !== activePointerId) return;
            activePointerId = null;
            suppressed = false;
        };

        el.addEventListener('pointerdown', handleDown);
        el.addEventListener('pointerup', handleUp);
        el.addEventListener('pointercancel', handleCancel);

        return () => {
            el.removeEventListener('pointerdown', handleDown);
            el.removeEventListener('pointerup', handleUp);
            el.removeEventListener('pointercancel', handleCancel);
        };
    }, [ref, onSwipeLeft, onSwipeRight, threshold, maxVertical]);
}
