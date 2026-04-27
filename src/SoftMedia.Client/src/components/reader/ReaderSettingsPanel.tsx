import { useEffect, type ReactNode } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { Minus, Plus, RotateCcw, X } from 'lucide-react';
import {
    useResetReaderPrefs,
    FONT_SIZE_MIN,
    FONT_SIZE_MAX,
    FONT_SIZE_STEP,
    ZOOM_PCT_MIN,
    ZOOM_PCT_MAX,
    ZOOM_PCT_STEP,
    type ZoomMode,
} from '../../store/readerStore';

interface ReaderSettingsPanelProps {
    open: boolean;
    onClose: () => void;
    /** Sections filled by ER-002/ER-020/ER-021 (Display, Typography, Theme). */
    children?: ReactNode;
}

/**
 * Right-anchored drawer hosting every reader preference control. Shipped as a
 * shell in ER-010 with only the chrome (header, close, reset) present; ER-002
 * adds the Display section (spread), ER-020 adds Typography (font/size/line/
 * margin), ER-021 adds Theme. Consumers pass their section controls as
 * children so this component stays agnostic about which prefs exist.
 *
 * Deliberately mirrors TocDrawer.tsx visually and behaviourally — same slide,
 * same backdrop, same Escape contract — so the reader's two overlay surfaces
 * feel like one idea.
 */
export default function ReaderSettingsPanel({ open, onClose, children }: ReaderSettingsPanelProps) {
    const resetReaderPrefs = useResetReaderPrefs();

    // Capture-phase Escape so we unwrap the drawer before the reader-level
    // Esc cascade (fullscreen → immersive → close) sees the keystroke.
    useEffect(() => {
        if (!open) return;
        const handler = (e: KeyboardEvent) => {
            if (e.key === 'Escape') {
                e.stopPropagation();
                onClose();
            }
        };
        window.addEventListener('keydown', handler, true);
        return () => window.removeEventListener('keydown', handler, true);
    }, [open, onClose]);

    return (
        <AnimatePresence>
            {open && (
                <>
                    <motion.div
                        key="settings-backdrop"
                        className="fixed inset-0 bg-black/40 z-40"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.aside
                        key="settings-drawer"
                        className="fixed top-0 right-0 h-full w-[360px] max-w-[90vw] bg-gray-800 shadow-2xl z-50 flex flex-col text-white"
                        role="dialog"
                        aria-label="Reader settings"
                        initial={{ x: '100%' }}
                        animate={{ x: 0 }}
                        exit={{ x: '100%' }}
                        transition={{ type: 'tween', duration: 0.2 }}
                    >
                        <div className="h-14 flex items-center justify-between px-4 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Reader settings</h3>
                            <button
                                type="button"
                                aria-label="Close reader settings"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>

                        <div className="flex-1 overflow-y-auto">
                            {children ? (
                                <div className="py-2">{children}</div>
                            ) : (
                                <p className="px-4 py-6 text-sm text-gray-400">
                                    No settings yet.
                                </p>
                            )}
                        </div>

                        <div className="shrink-0 border-t border-gray-700 px-4 py-3 flex justify-end">
                            <button
                                type="button"
                                onClick={resetReaderPrefs}
                                className="inline-flex items-center gap-2 min-h-[44px] px-3 py-2 text-sm rounded-md text-gray-200 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <RotateCcw size={16} />
                                Reset to defaults
                            </button>
                        </div>
                    </motion.aside>
                </>
            )}
        </AnimatePresence>
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Building blocks for panel sections. Consumers (ER-002, ER-020, ER-021)
// compose these into their sections rather than styling one-off controls per
// task. Colocated here — they're trivial wrappers and only used inside the
// panel — so the future theme refactor (ER-011) can pivot their visuals in
// one place.

interface SectionProps {
    title: string;
    description?: string;
    children: ReactNode;
}

export function PanelSection({ title, description, children }: SectionProps) {
    return (
        <section className="px-4 py-4 border-b border-gray-700 last:border-b-0">
            <h4 className="text-xs uppercase tracking-wide text-gray-400 mb-1">{title}</h4>
            {description && (
                <p className="text-xs text-gray-500 mb-3">{description}</p>
            )}
            <div className="flex flex-col gap-2">{children}</div>
        </section>
    );
}

interface SegmentedOption<T extends string> {
    value: T;
    label: string;
    /** Optional hint, shown as a title attribute — useful for longer options. */
    hint?: string;
}

interface SegmentedProps<T extends string> {
    label: string;
    value: T;
    options: ReadonlyArray<SegmentedOption<T>>;
    onChange: (next: T) => void;
}

/**
 * Number-stepper for the font-size control (ER-020). Clamps against the store's
 * min/max and advances by the store's step. Exposed as a reusable primitive in
 * case a future setting (zoom level in ER-030?) needs the same shape. `aria-
 * valuemin/max/now` carries the semantics that a plain +/- button pair lacks.
 */
interface FontSizeControlProps {
    value: number;
    onChange: (next: number) => void;
}

export function FontSizeControl({ value, onChange }: FontSizeControlProps) {
    const atMin = value <= FONT_SIZE_MIN;
    const atMax = value >= FONT_SIZE_MAX;
    return (
        <div>
            <div className="text-sm mb-1 text-gray-200">Font size</div>
            <div
                role="group"
                aria-label="Font size"
                className="flex items-center gap-2 bg-gray-900 rounded-lg p-1"
            >
                <button
                    type="button"
                    aria-label="Decrease font size"
                    disabled={atMin}
                    onClick={() => onChange(value - FONT_SIZE_STEP)}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-md text-gray-200 hover:bg-gray-700 disabled:opacity-30 disabled:hover:bg-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Minus size={18} />
                </button>
                <div
                    aria-live="polite"
                    aria-valuemin={FONT_SIZE_MIN}
                    aria-valuemax={FONT_SIZE_MAX}
                    aria-valuenow={value}
                    role="status"
                    className="flex-1 text-center font-mono text-sm text-gray-200 select-none"
                >
                    {value}%
                </div>
                <button
                    type="button"
                    aria-label="Increase font size"
                    disabled={atMax}
                    onClick={() => onChange(value + FONT_SIZE_STEP)}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-md text-gray-200 hover:bg-gray-700 disabled:opacity-30 disabled:hover:bg-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Plus size={18} />
                </button>
            </div>
        </div>
    );
}

/**
 * Zoom control (ER-030). Combines a `−` / current / `+` stepper for numeric
 * zoom with three preset buttons (Fit width / Fit page / 100%). Shares styling
 * with FontSizeControl so the panel feels cohesive. When zoom is a preset, the
 * stepper seeds itself from 100% on the next numeric step.
 */
interface ZoomControlProps {
    value: ZoomMode;
    onChange: (next: ZoomMode) => void;
}

export function ZoomControl({ value, onChange }: ZoomControlProps) {
    const asNumber = typeof value === 'number' ? value : 100;
    const atMin = typeof value === 'number' && value <= ZOOM_PCT_MIN;
    const atMax = typeof value === 'number' && value >= ZOOM_PCT_MAX;
    const labelText = typeof value === 'number'
        ? `${value}%`
        : value === 'fit-width' ? 'Fit width' : 'Fit page';

    const step = (delta: number) => {
        const next = Math.max(ZOOM_PCT_MIN, Math.min(ZOOM_PCT_MAX, asNumber + delta));
        onChange(next);
    };

    return (
        <div>
            <div className="text-sm mb-1 text-gray-200">Zoom</div>
            <div
                role="group"
                aria-label="Zoom"
                className="flex items-center gap-2 bg-gray-900 rounded-lg p-1 mb-2"
            >
                <button
                    type="button"
                    aria-label="Zoom out"
                    disabled={atMin}
                    onClick={() => step(-ZOOM_PCT_STEP)}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-md text-gray-200 hover:bg-gray-700 disabled:opacity-30 disabled:hover:bg-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Minus size={18} />
                </button>
                <div
                    aria-live="polite"
                    role="status"
                    className="flex-1 text-center font-mono text-sm text-gray-200 select-none"
                >
                    {labelText}
                </div>
                <button
                    type="button"
                    aria-label="Zoom in"
                    disabled={atMax}
                    onClick={() => step(ZOOM_PCT_STEP)}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-md text-gray-200 hover:bg-gray-700 disabled:opacity-30 disabled:hover:bg-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Plus size={18} />
                </button>
            </div>
            <div role="radiogroup" aria-label="Zoom preset" className="flex gap-1 bg-gray-900 rounded-lg p-1">
                {(['fit-width', 'fit-page'] as const).map((preset) => {
                    const selected = value === preset;
                    return (
                        <button
                            key={preset}
                            type="button"
                            role="radio"
                            aria-checked={selected}
                            onClick={() => onChange(preset)}
                            className={`flex-1 min-h-[44px] px-3 py-2 text-sm rounded-md transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                                selected ? 'bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow' : 'text-gray-200 hover:bg-gray-700'
                            }`}
                        >
                            {preset === 'fit-width' ? 'Fit width' : 'Fit page'}
                        </button>
                    );
                })}
                <button
                    type="button"
                    role="radio"
                    aria-checked={value === 100}
                    onClick={() => onChange(100)}
                    className={`flex-1 min-h-[44px] px-3 py-2 text-sm rounded-md transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                        value === 100 ? 'bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow' : 'text-gray-200 hover:bg-gray-700'
                    }`}
                >
                    100%
                </button>
            </div>
        </div>
    );
}

/**
 * Continuous slider control (ER-053). Uses a native `<input type="range">`
 * so keyboard arrow-key adjustment and screen-reader value announcements
 * come for free. The valueLabel callback formats the current value for a
 * small right-aligned display — "80%" for brightness, "+40%" for warmth.
 */
interface SliderControlProps {
    label: string;
    min: number;
    max: number;
    step: number;
    value: number;
    onChange: (next: number) => void;
    valueLabel?: (value: number) => string;
}

export function SliderControl({ label, min, max, step, value, onChange, valueLabel }: SliderControlProps) {
    return (
        <div>
            <div className="flex items-center justify-between mb-1">
                <span className="text-sm text-gray-200">{label}</span>
                {valueLabel && (
                    <span className="text-xs font-mono text-gray-400">{valueLabel(value)}</span>
                )}
            </div>
            <input
                type="range"
                min={min}
                max={max}
                step={step}
                value={value}
                onChange={(e) => onChange(parseFloat(e.target.value))}
                className="w-full accent-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded"
                aria-label={label}
                aria-valuemin={min}
                aria-valuemax={max}
                aria-valuenow={value}
            />
        </div>
    );
}

/**
 * Segmented-control primitive used by every radio-style setting (spread,
 * line-height, margin, theme). Implemented with `role="radiogroup"` so screen
 * readers announce it consistently across sections. Arrow-key navigation is
 * the native browser-radio behaviour — we don't reimplement it.
 */
export function SegmentedControl<T extends string>({ label, value, options, onChange }: SegmentedProps<T>) {
    return (
        <div>
            <div className="text-sm mb-1 text-gray-200">{label}</div>
            <div role="radiogroup" aria-label={label} className="flex gap-1 bg-gray-900 rounded-lg p-1">
                {options.map((opt) => {
                    const selected = opt.value === value;
                    return (
                        <button
                            key={opt.value}
                            type="button"
                            role="radio"
                            aria-checked={selected}
                            title={opt.hint}
                            onClick={() => onChange(opt.value)}
                            className={`flex-1 min-h-[44px] px-3 py-2 text-sm rounded-md transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                                selected
                                    ? 'bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow'
                                    : 'text-gray-200 hover:bg-gray-700'
                            }`}
                        >
                            {opt.label}
                        </button>
                    );
                })}
            </div>
        </div>
    );
}
