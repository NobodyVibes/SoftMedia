import { create } from 'zustand';
import { persist } from 'zustand/middleware';

/**
 * User-facing reading preferences. All values are global today; ER-012 will add
 * a per-book overrides layer on top. Fields marked "reserved" are typed now so
 * the stored-shape schema stabilises before the consumers land — keeping the
 * version bump on its first real introduction (when persistence of an old
 * shape would otherwise break existing users).
 */
export type SpreadMode = 'single' | 'double';
export type ReaderTheme = 'dark' | 'sepia' | 'high-contrast';
export type ReaderFontFamily =
    | 'inter'
    | 'georgia'
    | 'merriweather'
    | 'open-dyslexic'
    | 'system-serif'
    | 'system-sans';
export type LineHeightMode = 'tight' | 'normal' | 'loose';
export type MarginMode = 'narrow' | 'normal' | 'wide';
export type ZoomMode = 'fit-width' | 'fit-page' | number;

export interface ReaderPrefs {
    schemaVersion: 1;
    spread: SpreadMode;               // ER-002
    theme: ReaderTheme;               // ER-021
    fontFamily: ReaderFontFamily;     // ER-020
    fontSize: number;                 // ER-020 — percent, 80–160, step 10
    lineHeight: LineHeightMode;       // ER-020
    margin: MarginMode;               // ER-020
    overridePublisher: boolean;       // ER-022 — win vs publisher CSS
    brightness: number;               // ER-053 — 0.3 (dim) to 1.0 (full)
    warmth: number;                   // ER-053 — 0 (neutral) to 1 (amber-tinted)
    // Reserved for later milestones. Declared here so storage shape is stable.
    immersive: boolean;               // ER-007 persistence follow-up
    zoom: ZoomMode;                   // ER-030
    rtl: boolean;                     // ER-031
    ttsVoice: string | null;          // ER-050
    ttsRate: number;                  // ER-050 — 0.5 to 2.0, default 1.0
}

const DEFAULTS: ReaderPrefs = {
    schemaVersion: 1,
    spread: 'single',
    theme: 'dark',
    fontFamily: 'inter',
    fontSize: 100,
    lineHeight: 'normal',
    margin: 'normal',
    // Default ON so a fresh install enforces SoftMedia's theme over any
    // publisher CSS — the dark palette being defeated is worse than a
    // publisher-intended serif being overridden.
    overridePublisher: true,
    brightness: 1.0,
    warmth: 0,
    immersive: false,
    zoom: 'fit-width',
    rtl: false,
    ttsVoice: null,
    ttsRate: 1.0,
};

export const FONT_SIZE_MIN = 80;
export const FONT_SIZE_MAX = 160;
export const FONT_SIZE_STEP = 10;

export const ZOOM_PCT_MIN = 50;
export const ZOOM_PCT_MAX = 400;
export const ZOOM_PCT_STEP = 25;

interface ReaderState extends ReaderPrefs {
    setSpread: (v: SpreadMode) => void;
    setTheme: (v: ReaderTheme) => void;
    setFontFamily: (v: ReaderFontFamily) => void;
    setFontSize: (v: number) => void;
    setLineHeight: (v: LineHeightMode) => void;
    setMargin: (v: MarginMode) => void;
    setOverridePublisher: (v: boolean) => void;
    setBrightness: (v: number) => void;
    setWarmth: (v: number) => void;
    setImmersive: (v: boolean) => void;
    setZoom: (v: ZoomMode) => void;
    setRtl: (v: boolean) => void;
    setTtsVoice: (v: string | null) => void;
    setTtsRate: (v: number) => void;
    resetReaderPrefs: () => void;
}

const clampFontSize = (v: number): number => {
    if (!Number.isFinite(v)) return DEFAULTS.fontSize;
    const rounded = Math.round(v / FONT_SIZE_STEP) * FONT_SIZE_STEP;
    return Math.min(FONT_SIZE_MAX, Math.max(FONT_SIZE_MIN, rounded));
};

export const useReaderStore = create<ReaderState>()(
    persist(
        (set) => ({
            ...DEFAULTS,
            setSpread: (v) => set({ spread: v }),
            setTheme: (v) => set({ theme: v }),
            setFontFamily: (v) => set({ fontFamily: v }),
            setFontSize: (v) => set({ fontSize: clampFontSize(v) }),
            setLineHeight: (v) => set({ lineHeight: v }),
            setMargin: (v) => set({ margin: v }),
            setOverridePublisher: (v) => set({ overridePublisher: v }),
            setBrightness: (v) => {
                // Clamp defensively — the slider UI already constrains, but a
                // future caller (persisted-old-shape migration, URL deep-link)
                // may hand us out-of-range values.
                const clamped = Math.max(0.3, Math.min(1.0, Number.isFinite(v) ? v : 1.0));
                set({ brightness: clamped });
            },
            setWarmth: (v) => {
                const clamped = Math.max(0, Math.min(1, Number.isFinite(v) ? v : 0));
                set({ warmth: clamped });
            },
            setImmersive: (v) => set({ immersive: v }),
            setZoom: (v) => set({ zoom: v }),
            setRtl: (v) => set({ rtl: v }),
            setTtsVoice: (v) => set({ ttsVoice: v }),
            setTtsRate: (v) => {
                // Browser speechSynthesis accepts 0.1–10; real-world useful
                // range is 0.5–2.0. Clamp defensively.
                const clamped = Math.max(0.5, Math.min(2.0, Number.isFinite(v) ? v : 1.0));
                set({ ttsRate: clamped });
            },
            resetReaderPrefs: () => set({ ...DEFAULTS }),
        }),
        {
            name: 'softmedia.reader.prefs.v1',
            version: 1,
            // Unknown / future schemas snap back to defaults. A brand-new shape
            // in v2 must ship with its own migrate branch. We never attempt a
            // lossy partial recovery — in a self-hosted app losing preferences
            // is far less costly than a silently corrupt state.
            migrate: (persisted, fromVersion) => {
                if (fromVersion === 1 && persisted && typeof persisted === 'object') {
                    const p = persisted as Partial<ReaderPrefs>;
                    return { ...DEFAULTS, ...p, schemaVersion: 1 };
                }
                return { ...DEFAULTS };
            },
            // Don't persist the action functions — Zustand already excludes
            // them, but being explicit guards against someone refactoring the
            // state shape and forgetting. Only the plain fields travel.
            partialize: (s): ReaderPrefs => ({
                schemaVersion: s.schemaVersion,
                spread: s.spread,
                theme: s.theme,
                fontFamily: s.fontFamily,
                fontSize: s.fontSize,
                lineHeight: s.lineHeight,
                margin: s.margin,
                overridePublisher: s.overridePublisher,
                brightness: s.brightness,
                warmth: s.warmth,
                immersive: s.immersive,
                zoom: s.zoom,
                rtl: s.rtl,
                ttsVoice: s.ttsVoice,
                ttsRate: s.ttsRate,
            }),
        },
    ),
);

// ── Granular selectors ───────────────────────────────────────────────────────
// Each selector returns a single primitive (or stable function reference) so
// consuming components re-render only when the specific field they read
// changes. Setter references are stable across renders — Zustand creates the
// setter closures once inside the store creator — so subscribing to a setter
// with `useReaderStore((s) => s.setX)` will never trigger a re-render.
//
// Call-site pattern:
//   const spread = useSpread();
//   const setSpread = useSetSpread();
// Two hook calls but zero tuple-literal allocation, zero shallow compare.

export const useSpread = (): SpreadMode => useReaderStore((s) => s.spread);
export const useSetSpread = () => useReaderStore((s) => s.setSpread);

export const useReaderTheme = (): ReaderTheme => useReaderStore((s) => s.theme);
export const useSetReaderTheme = () => useReaderStore((s) => s.setTheme);

export const useFontFamily = (): ReaderFontFamily => useReaderStore((s) => s.fontFamily);
export const useSetFontFamily = () => useReaderStore((s) => s.setFontFamily);

export const useFontSize = (): number => useReaderStore((s) => s.fontSize);
export const useSetFontSize = () => useReaderStore((s) => s.setFontSize);

export const useLineHeight = (): LineHeightMode => useReaderStore((s) => s.lineHeight);
export const useSetLineHeight = () => useReaderStore((s) => s.setLineHeight);

export const useMargin = (): MarginMode => useReaderStore((s) => s.margin);
export const useSetMargin = () => useReaderStore((s) => s.setMargin);

export const useOverridePublisher = (): boolean => useReaderStore((s) => s.overridePublisher);
export const useSetOverridePublisher = () => useReaderStore((s) => s.setOverridePublisher);

export const useRtl = (): boolean => useReaderStore((s) => s.rtl);
export const useSetRtl = () => useReaderStore((s) => s.setRtl);

export const useBrightness = (): number => useReaderStore((s) => s.brightness);
export const useSetBrightness = () => useReaderStore((s) => s.setBrightness);

export const useWarmth = (): number => useReaderStore((s) => s.warmth);
export const useSetWarmth = () => useReaderStore((s) => s.setWarmth);

export const useTtsVoice = (): string | null => useReaderStore((s) => s.ttsVoice);
export const useSetTtsVoice = () => useReaderStore((s) => s.setTtsVoice);

export const useTtsRate = (): number => useReaderStore((s) => s.ttsRate);
export const useSetTtsRate = () => useReaderStore((s) => s.setTtsRate);

export const useZoom = (): ZoomMode => useReaderStore((s) => s.zoom);
export const useSetZoom = () => useReaderStore((s) => s.setZoom);

export const useResetReaderPrefs = () => useReaderStore((s) => s.resetReaderPrefs);
