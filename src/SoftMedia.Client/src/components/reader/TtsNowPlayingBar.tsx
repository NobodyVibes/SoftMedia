import { useEffect, useRef, useState } from 'react';
import {
    Pause,
    Play,
    SkipBack,
    SkipForward,
    Timer,
    X,
} from 'lucide-react';

/**
 * A pill of mid-listen controls — skip / play-pause / skip / speed / sleep /
 * stop — laid out horizontally. The component does NOT position itself:
 * callers are expected to render it inside a container that owns bottom
 * placement and visibility (see BookReader's bottom-chrome flex wrapper,
 * where this pill sits next to PageControls and shares its immersive-hide
 * behavior). Rendering its own floating container previously caused the bar
 * to overlap the reader content; the parent now owns layout space.
 */

/** Speed presets the cycle button walks through. Matches the settings
 *  slider's `min` and `max` (0.5 – 2.0) at steps people actually use. */
const SPEED_PRESETS = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0] as const;

function nextRate(current: number): number {
    // Find the next preset strictly greater than `current`; wrap to the
    // first. Works even when `current` is a non-preset (e.g. slider was
    // dragged to 1.1) — we land on the next preset above.
    const next = SPEED_PRESETS.find((p) => p > current + 0.001);
    return next ?? SPEED_PRESETS[0];
}

export type SleepTimerMode = 'off' | '5m' | '15m' | '30m' | 'eoc';

interface TtsNowPlayingBarProps {
    visible: boolean;
    chapter: string | null;
    isPaused: boolean;
    rate: number;
    onRateChange: (rate: number) => void;
    onPauseToggle: () => void;
    onStop: () => void;
    onSkipBack: () => void;
    onSkipForward: () => void;
    sleepTimerMode: SleepTimerMode;
    /** Milliseconds remaining, or null if no timed mode (off / eoc). */
    sleepTimerRemainingMs: number | null;
    onSetSleepTimer: (mode: SleepTimerMode) => void;
}

export default function TtsNowPlayingBar({
    visible,
    chapter,
    isPaused,
    rate,
    onRateChange,
    onPauseToggle,
    onStop,
    onSkipBack,
    onSkipForward,
    sleepTimerMode,
    sleepTimerRemainingMs,
    onSetSleepTimer,
}: TtsNowPlayingBarProps) {
    const [timerMenuOpen, setTimerMenuOpen] = useState(false);
    const timerMenuRef = useRef<HTMLDivElement | null>(null);

    // Close the timer popover on outside click. We only attach the listener
    // while the menu is open so we don't pay for it otherwise.
    useEffect(() => {
        if (!timerMenuOpen) return;
        const onDown = (e: MouseEvent) => {
            if (!timerMenuRef.current?.contains(e.target as Node)) {
                setTimerMenuOpen(false);
            }
        };
        window.addEventListener('mousedown', onDown);
        return () => window.removeEventListener('mousedown', onDown);
    }, [timerMenuOpen]);

    // Close the timer popover if the bar disappears.
    useEffect(() => {
        if (!visible && timerMenuOpen) setTimerMenuOpen(false);
    }, [visible, timerMenuOpen]);

    if (!visible) return null;

    // Human-readable timer badge: minutes remaining for numeric modes, a
    // static "chapter" hint for end-of-chapter mode. We round up so the
    // last displayed value lingers briefly before hitting 0.
    const timerBadge = sleepTimerMode === 'eoc'
        ? 'chapter'
        : sleepTimerRemainingMs !== null && sleepTimerRemainingMs > 0
            ? `${Math.max(1, Math.ceil(sleepTimerRemainingMs / 60_000))}m`
            : null;

    return (
        <div
            role="toolbar"
            aria-label="Listen controls"
            // No positioning here: the parent places this pill. Keeping the
            // pill self-positioned (fixed/absolute) caused it to float over
            // whatever content was behind it — almost always the reader's
            // text body.
            className="flex items-center gap-1 pl-1 pr-1 py-1 rounded-full bg-gray-900/90 backdrop-blur-md shadow-2xl border border-gray-700/60 text-white"
        >
            {chapter && (
                <div className="hidden sm:block px-3 py-1 text-xs max-w-[180px] truncate text-gray-300 border-r border-gray-700/60">
                    {chapter}
                </div>
            )}

            <button
                type="button"
                aria-label="Skip back one sentence"
                title="Previous sentence"
                onClick={onSkipBack}
                className="min-w-[36px] min-h-[36px] flex items-center justify-center rounded-full hover:bg-gray-700/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <SkipBack size={18} />
            </button>
            <button
                type="button"
                aria-label={isPaused ? 'Resume listening' : 'Pause listening'}
                title={isPaused ? 'Resume' : 'Pause'}
                onClick={onPauseToggle}
                className="min-w-[40px] min-h-[40px] flex items-center justify-center rounded-full bg-gray-800 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                {isPaused ? <Play size={20} /> : <Pause size={20} />}
            </button>
            <button
                type="button"
                aria-label="Skip forward one sentence"
                title="Next sentence"
                onClick={onSkipForward}
                className="min-w-[36px] min-h-[36px] flex items-center justify-center rounded-full hover:bg-gray-700/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <SkipForward size={18} />
            </button>

            <div className="w-px h-6 bg-gray-700/60 mx-1" aria-hidden />

            <button
                type="button"
                aria-label={`Playback speed ${rate.toFixed(2)} times. Click to cycle.`}
                title="Cycle playback speed"
                onClick={() => onRateChange(nextRate(rate))}
                className="min-h-[32px] px-2 text-xs font-semibold rounded-full bg-gray-800 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                {rate.toFixed(rate % 1 === 0 ? 1 : 2)}×
            </button>

            <div ref={timerMenuRef} className="relative">
                <button
                    type="button"
                    aria-label="Sleep timer"
                    title={sleepTimerMode === 'off' ? 'Set sleep timer' : `Sleep timer: ${timerBadge}`}
                    aria-expanded={timerMenuOpen}
                    onClick={() => setTimerMenuOpen((v) => !v)}
                    className={`min-h-[32px] min-w-[36px] px-2 flex items-center gap-1 rounded-full hover:bg-gray-700/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                        sleepTimerMode !== 'off' ? 'text-amber-300' : 'text-white'
                    }`}
                >
                    <Timer size={16} />
                    {timerBadge && <span className="text-[10px] font-semibold">{timerBadge}</span>}
                </button>
                {timerMenuOpen && (
                    <div
                        role="menu"
                        className="absolute bottom-full mb-2 right-0 min-w-[160px] rounded-xl bg-gray-900/95 backdrop-blur-md border border-gray-700/60 shadow-2xl overflow-hidden"
                    >
                        {([
                            ['off', 'Off'],
                            ['5m', '5 minutes'],
                            ['15m', '15 minutes'],
                            ['30m', '30 minutes'],
                            ['eoc', 'End of chapter'],
                        ] as ReadonlyArray<[SleepTimerMode, string]>).map(([mode, label]) => (
                            <button
                                key={mode}
                                type="button"
                                role="menuitemradio"
                                aria-checked={sleepTimerMode === mode}
                                onClick={() => {
                                    onSetSleepTimer(mode);
                                    setTimerMenuOpen(false);
                                }}
                                className={`w-full text-left text-sm px-3 py-2 hover:bg-gray-800 focus-visible:outline-none focus-visible:bg-gray-800 ${
                                    sleepTimerMode === mode ? 'text-amber-300' : 'text-gray-200'
                                }`}
                            >
                                {label}
                            </button>
                        ))}
                    </div>
                )}
            </div>

            <button
                type="button"
                aria-label="Stop listening"
                title="Stop"
                onClick={onStop}
                className="min-w-[36px] min-h-[36px] flex items-center justify-center rounded-full hover:bg-red-700/40 text-red-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400"
            >
                <X size={18} />
            </button>
        </div>
    );
}
