import { useEffect, useState } from 'react';
import { SkipForward } from 'lucide-react';

interface SkipSegmentPillProps {
    /** Visible label, e.g. "Skip Intro" or "Skip Credits". */
    label: string;
    /** True when the playhead is inside the segment (caller decides). */
    visible: boolean;
    /** Called when the user clicks the pill or presses the keyboard shortcut. */
    onSkip: () => void;
    /**
     * Optional accessible label override. Defaults to `label` so screen readers
     * announce the same text the sighted user sees.
     */
    ariaLabel?: string;
}

/**
 * Bottom-right "Skip Intro" / "Skip Credits" pill shown while the playhead is
 * inside a detected (or chapter-derived) intro / credits segment. Universal
 * client rules (SDD §8.3) apply:
 *   - native <button> so screen readers and keyboard users get free affordance
 *   - hover paired with focus-visible (signature gradient ring)
 *   - ≥44×44 px touch target
 *   - `S` keyboard shortcut to fire the skip without leaving the player
 *
 * The pill auto-fades out 8 seconds after appearing so it doesn't loiter on
 * top of the video for the entirety of a long intro. If the user seeks or the
 * segment ends, the parent will toggle `visible=false` and unmount us.
 */
export function SkipSegmentPill({ label, visible, onSkip, ariaLabel }: SkipSegmentPillProps) {
    // Local "should we show" state separate from the prop. We start visible when
    // the parent says we are inside the segment, then auto-fade after 8 s. If
    // the parent flips visible back to true (seek into a different segment), we
    // reset and show again.
    const [showing, setShowing] = useState(visible);

    // Track the (visible, label) signal and resync `showing` during render —
    // a transition from intro→credits (label change) re-shows and re-arms just
    // as a fresh visible=true does. Only the auto-fade timer stays in the
    // effect, because a timeout is genuinely external work; its callback
    // setting state is the documented pattern.
    const [lastSignal, setLastSignal] = useState({ visible, label });
    if (visible !== lastSignal.visible || label !== lastSignal.label) {
        setLastSignal({ visible, label });
        setShowing(visible);
    }

    useEffect(() => {
        if (!visible) return;
        const t = setTimeout(() => setShowing(false), 8000);
        return () => clearTimeout(t);
    }, [visible, label]); // include label so a transition from intro→credits resets the timer

    // Global keyboard shortcut: `S` skips. We only listen while `showing` so
    // typing `s` in some other context (a search field, etc.) doesn't fire.
    useEffect(() => {
        if (!showing) return;

        const handler = (e: KeyboardEvent) => {
            if (e.key !== 's' && e.key !== 'S') return;
            // Don't capture when the user is actively typing.
            const target = e.target as HTMLElement | null;
            if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
                return;
            }
            e.preventDefault();
            onSkip();
        };

        window.addEventListener('keydown', handler);
        return () => window.removeEventListener('keydown', handler);
    }, [showing, onSkip]);

    if (!showing) return null;

    return (
        <button
            type="button"
            onClick={onSkip}
            aria-label={ariaLabel ?? label}
            title={`${label} (S)`}
            className="absolute bottom-24 right-6 z-40 inline-flex items-center gap-2 px-5 py-3 min-w-[44px] min-h-[44px] rounded-full bg-black/80 backdrop-blur-md border border-white/15 text-white text-sm font-medium shadow-lg hover:bg-black/90 hover:border-white/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-offset-black/80 focus-visible:ring-blue-400 transition-all duration-200"
        >
            <SkipForward size={18} />
            <span>{label}</span>
            <kbd className="ml-1 px-1.5 py-0.5 text-[10px] font-mono rounded bg-white/10 border border-white/20 text-white/70">S</kbd>
        </button>
    );
}
