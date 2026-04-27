import { useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { X } from 'lucide-react';
import { SHORTCUTS, type ShortcutSpec } from './shortcuts';

interface ShortcutHelpSheetProps {
    open: boolean;
    onClose: () => void;
}

/**
 * Centered modal listing every reader keyboard shortcut. The content is
 * driven entirely by the SHORTCUTS constant so changes there reflect here
 * automatically. Opens via the `?` shortcut; closes on Escape / backdrop /
 * X button.
 */
export default function ShortcutHelpSheet({ open, onClose }: ShortcutHelpSheetProps) {
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

    const groups = groupShortcuts(SHORTCUTS);

    return (
        <AnimatePresence>
            {open && (
                <>
                    <motion.div
                        key="help-backdrop"
                        className="fixed inset-0 bg-black/60 z-[70]"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.div
                        key="help-sheet"
                        role="dialog"
                        aria-label="Keyboard shortcuts"
                        className="fixed top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 w-[540px] max-w-[94vw] max-h-[80vh] bg-gray-800 text-white rounded-xl shadow-2xl z-[71] flex flex-col"
                        initial={{ opacity: 0, scale: 0.96 }}
                        animate={{ opacity: 1, scale: 1 }}
                        exit={{ opacity: 0, scale: 0.96 }}
                        transition={{ duration: 0.15 }}
                    >
                        <div className="h-14 flex items-center justify-between px-5 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Keyboard shortcuts</h3>
                            <button
                                type="button"
                                aria-label="Close shortcuts"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>
                        <div className="flex-1 overflow-y-auto px-5 py-4">
                            {Object.entries(groups).map(([group, rows]) => (
                                <section key={group} className="mb-5 last:mb-0">
                                    <h4 className="text-xs uppercase tracking-wide text-gray-400 mb-2">
                                        {group}
                                    </h4>
                                    <ul className="list-none m-0 p-0">
                                        {rows.map((s, i) => (
                                            <li
                                                key={`${group}-${i}`}
                                                className="flex items-center gap-3 py-1.5 border-b border-gray-700/50 last:border-b-0"
                                            >
                                                <kbd className="inline-block min-w-[120px] px-2 py-1 rounded bg-gray-900 text-gray-100 text-xs font-mono text-center">
                                                    {s.displayKey}
                                                </kbd>
                                                <span className="text-sm text-gray-200">
                                                    {s.description}
                                                </span>
                                            </li>
                                        ))}
                                    </ul>
                                </section>
                            ))}
                        </div>
                    </motion.div>
                </>
            )}
        </AnimatePresence>
    );
}

function groupShortcuts(list: ReadonlyArray<ShortcutSpec>): Record<string, ShortcutSpec[]> {
    const out: Record<string, ShortcutSpec[]> = {};
    for (const s of list) {
        (out[s.group] ??= []).push(s);
    }
    return out;
}
