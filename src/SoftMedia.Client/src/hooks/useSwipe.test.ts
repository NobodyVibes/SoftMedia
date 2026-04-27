import { renderHook } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { useRef } from 'react';
import { useSwipe } from './useSwipe';

/**
 * Dispatches a pointerdown + pointerup pair at given coordinates. The DOM
 * pointer-event constructor isn't universally available under jsdom, so we
 * fall back to a synthesised MouseEvent tagged with the pointerId via a
 * typed wrapper — the hook only reads clientX/Y/pointerId.
 */
function dispatchSwipe(
    el: Element,
    fromX: number,
    fromY: number,
    toX: number,
    toY: number,
    pointerId = 1,
) {
    const mk = (type: string, x: number, y: number) => {
        const ev = new Event(type, { bubbles: true, cancelable: true });
        Object.defineProperty(ev, 'clientX', { value: x });
        Object.defineProperty(ev, 'clientY', { value: y });
        Object.defineProperty(ev, 'pointerId', { value: pointerId });
        Object.defineProperty(ev, 'target', { value: el });
        return ev;
    };
    el.dispatchEvent(mk('pointerdown', fromX, fromY));
    el.dispatchEvent(mk('pointerup', toX, toY));
}

function setupHook() {
    const el = document.createElement('div');
    document.body.appendChild(el);
    const onSwipeLeft = vi.fn();
    const onSwipeRight = vi.fn();

    renderHook(() => {
        // The hook takes a ref; we fabricate one that points at our test el.
        const ref = useRef<HTMLDivElement | null>(el);
        useSwipe(ref, { onSwipeLeft, onSwipeRight });
    });

    return { el, onSwipeLeft, onSwipeRight };
}

describe('useSwipe', () => {
    it('fires onSwipeLeft when pointer moves strongly left', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        dispatchSwipe(el, 300, 100, 100, 105);
        expect(onSwipeLeft).toHaveBeenCalledTimes(1);
        expect(onSwipeRight).not.toHaveBeenCalled();
    });

    it('fires onSwipeRight when pointer moves strongly right', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        dispatchSwipe(el, 100, 100, 300, 98);
        expect(onSwipeRight).toHaveBeenCalledTimes(1);
        expect(onSwipeLeft).not.toHaveBeenCalled();
    });

    it('ignores vertical-dominant gestures so page scrolling still works', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        dispatchSwipe(el, 100, 100, 140, 300); // mostly vertical
        expect(onSwipeLeft).not.toHaveBeenCalled();
        expect(onSwipeRight).not.toHaveBeenCalled();
    });

    it('ignores short taps that are below threshold', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        dispatchSwipe(el, 100, 100, 120, 102);
        expect(onSwipeLeft).not.toHaveBeenCalled();
        expect(onSwipeRight).not.toHaveBeenCalled();
    });

    it('ignores swipes starting on inputs (so form fields stay usable)', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        const input = document.createElement('input');
        el.appendChild(input);
        dispatchSwipe(input, 300, 100, 100, 100);
        expect(onSwipeLeft).not.toHaveBeenCalled();
        expect(onSwipeRight).not.toHaveBeenCalled();
    });

    it('ignores swipes on elements opted out via data-no-swipe', () => {
        const { el, onSwipeLeft, onSwipeRight } = setupHook();
        const optOut = document.createElement('div');
        optOut.setAttribute('data-no-swipe', '');
        el.appendChild(optOut);
        dispatchSwipe(optOut, 300, 100, 100, 100);
        expect(onSwipeLeft).not.toHaveBeenCalled();
        expect(onSwipeRight).not.toHaveBeenCalled();
    });
});
