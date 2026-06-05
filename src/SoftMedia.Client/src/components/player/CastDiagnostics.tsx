import type { CastReadiness, CastCheck } from '../../hooks/castReadiness';

const STATUS_ICON: Record<CastCheck['status'], string> = { ok: '✓', warn: '!', fail: '✕' };
const STATUS_COLOR: Record<CastCheck['status'], string> = {
    ok: 'text-green-400',
    warn: 'text-yellow-400',
    fail: 'text-red-400',
};

/**
 * Compact "why can't I cast?" panel (CC-WI-005). Anchored above the cast control; lists the
 * three things a cast needs (reachable HTTPS, a Cast-capable browser, and an actual Cast device
 * on the network) so the user knows exactly what's missing.
 */
export function CastDiagnostics({ readiness, onClose }: { readiness: CastReadiness; onClose: () => void }) {
    return (
        <div
            role="dialog"
            aria-label="Cast readiness"
            className="absolute bottom-12 right-0 z-[60] w-80 max-w-[90vw] rounded-xl border border-white/10 bg-[#15151b] shadow-2xl p-4 text-left"
        >
            <div className="flex items-start justify-between gap-2 mb-2">
                <h3 className="text-sm font-semibold text-white">Cast readiness</h3>
                <button
                    type="button"
                    onClick={onClose}
                    aria-label="Close cast readiness"
                    className="text-white/50 hover:text-white text-sm leading-none p-1 -m-1 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    ✕
                </button>
            </div>
            <p className="text-xs text-white/70 mb-3">{readiness.headline}</p>
            <ul className="space-y-2.5">
                {readiness.checks.map((c) => (
                    <li key={c.label} className="flex gap-2.5">
                        <span className={`mt-0.5 font-bold ${STATUS_COLOR[c.status]}`} aria-hidden="true">{STATUS_ICON[c.status]}</span>
                        <span className="min-w-0">
                            <span className="block text-xs font-medium text-white">{c.label}</span>
                            <span className="block text-[11px] leading-snug text-white/60">{c.detail}</span>
                        </span>
                    </li>
                ))}
            </ul>
        </div>
    );
}
