import { beforeEach, describe, expect, it } from 'vitest';
import { act } from '@testing-library/react';
import {
    useReaderStore,
    FONT_SIZE_MIN,
    FONT_SIZE_MAX,
    FONT_SIZE_STEP,
} from './readerStore';

/**
 * The persist middleware registers with `localStorage`. jsdom provides an
 * implementation, but tests must clear it between cases or stored prefs from
 * one test leak into the next and turn "default shape" assertions into lies.
 */
const STORAGE_KEY = 'softmedia.reader.prefs.v1';

function resetStoreAndStorage() {
    window.localStorage.removeItem(STORAGE_KEY);
    act(() => {
        useReaderStore.getState().resetReaderPrefs();
    });
}

describe('readerStore — shape', () => {
    beforeEach(resetStoreAndStorage);

    it('emits the expected default shape', () => {
        const s = useReaderStore.getState();
        expect(s.schemaVersion).toBe(1);
        expect(s.spread).toBe('single');
        expect(s.theme).toBe('dark');
        expect(s.fontFamily).toBe('inter');
        expect(s.fontSize).toBe(100);
        expect(s.lineHeight).toBe('normal');
        expect(s.margin).toBe('normal');
        expect(s.immersive).toBe(false);
        expect(s.zoom).toBe('fit-width');
        expect(s.rtl).toBe(false);
        expect(s.ttsVoice).toBeNull();
    });
});

describe('readerStore — setters', () => {
    beforeEach(resetStoreAndStorage);

    it('updates a single field without disturbing the rest', () => {
        act(() => {
            useReaderStore.getState().setTheme('sepia');
        });
        const s = useReaderStore.getState();
        expect(s.theme).toBe('sepia');
        // Sanity check — other fields unchanged.
        expect(s.spread).toBe('single');
        expect(s.fontSize).toBe(100);
    });

    it('clamps font size to the configured range and step', () => {
        const { setFontSize } = useReaderStore.getState();
        act(() => setFontSize(50));
        expect(useReaderStore.getState().fontSize).toBe(FONT_SIZE_MIN);

        act(() => setFontSize(400));
        expect(useReaderStore.getState().fontSize).toBe(FONT_SIZE_MAX);

        act(() => setFontSize(137));
        // 137 → nearest step of 10 is 140 (rounded to step).
        expect(useReaderStore.getState().fontSize).toBe(140);
        expect(useReaderStore.getState().fontSize % FONT_SIZE_STEP).toBe(0);
    });

    it('rejects non-finite font sizes by falling back to the default', () => {
        act(() => useReaderStore.getState().setFontSize(Number.NaN));
        expect(useReaderStore.getState().fontSize).toBe(100);
    });

    it('resetReaderPrefs reverts every field to its default', () => {
        act(() => {
            const s = useReaderStore.getState();
            s.setSpread('double');
            s.setTheme('high-contrast');
            s.setFontSize(140);
            s.setLineHeight('loose');
        });

        act(() => useReaderStore.getState().resetReaderPrefs());

        const s = useReaderStore.getState();
        expect(s.spread).toBe('single');
        expect(s.theme).toBe('dark');
        expect(s.fontSize).toBe(100);
        expect(s.lineHeight).toBe('normal');
    });
});

describe('readerStore — persistence', () => {
    beforeEach(resetStoreAndStorage);

    it('writes to localStorage under the versioned key on change', () => {
        act(() => useReaderStore.getState().setTheme('sepia'));
        const raw = window.localStorage.getItem(STORAGE_KEY);
        expect(raw).not.toBeNull();
        const parsed = JSON.parse(raw!);
        // Zustand wraps persisted state in { state, version } — reach through.
        expect(parsed.version).toBe(1);
        expect(parsed.state.theme).toBe('sepia');
    });

    it('excludes setter functions from the persisted payload', () => {
        act(() => useReaderStore.getState().setSpread('double'));
        const raw = window.localStorage.getItem(STORAGE_KEY);
        const parsed = JSON.parse(raw!);
        // Every value under state.* must be a primitive or null. A serialised
        // function would look like {} after JSON round-trip — catching either
        // means the partialize boundary leaked. Null is allowed (ttsVoice).
        for (const [k, v] of Object.entries(parsed.state)) {
            expect(typeof v, `${k} should be primitive`).not.toBe('function');
            if (v !== null) {
                expect(typeof v, `${k} should be primitive`).not.toBe('object');
            }
        }
    });
});
