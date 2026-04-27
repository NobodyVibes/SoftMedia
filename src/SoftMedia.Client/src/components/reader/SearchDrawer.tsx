import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { Loader2, Search, X } from 'lucide-react';

export interface SearchHit {
    /** Opaque location string — EPUB CFI or `pdf:page:<n>`. Consumer handles jump. */
    key: string;
    /** Short context snippet, ideally the matched phrase surrounded by a few words. */
    excerpt: string;
    /** Optional leading label — section name (EPUB) or page number (PDF). */
    label?: string;
}

interface SearchDrawerProps {
    open: boolean;
    /** True while the provider is mid-search. Drawer renders a spinner in-place. */
    busy: boolean;
    hits: SearchHit[];
    /** Invoked on every keystroke (debounced by the caller if desired). */
    onQueryChange: (query: string) => void;
    onJump: (hit: SearchHit) => void;
    onClose: () => void;
    /** Null or empty disables search input (e.g., CBZ). */
    disabledReason?: string | null;
}

/**
 * Right-anchored drawer hosting in-book search. Visual grammar matches
 * TocDrawer / BookmarksDrawer / ReaderSettingsPanel so the reader has one
 * consistent overlay idiom. The drawer itself is UI-only — search providers
 * live in the consuming BookReader (EPUB uses epub.js book.search, PDF uses
 * pdf.js find controller). That split lets the drawer stay format-agnostic.
 */
export default function SearchDrawer({
    open,
    busy,
    hits,
    onQueryChange,
    onJump,
    onClose,
    disabledReason,
}: SearchDrawerProps) {
    const inputRef = useRef<HTMLInputElement | null>(null);
    const [query, setQuery] = useState('');

    // Escape closes. Capture-phase like the sibling drawers so it doesn't leak
    // into the reader's own Esc cascade.
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

    // Focus the input when the drawer opens so the user can start typing
    // immediately. Running inside AnimatePresence's mount works because the
    // motion.aside mounts synchronously; the transition only animates transform.
    useEffect(() => {
        if (open) {
            // One frame of slack to let the focus land after the slide animation.
            const id = window.setTimeout(() => inputRef.current?.focus(), 50);
            return () => window.clearTimeout(id);
        }
    }, [open]);

    const onInput = (next: string) => {
        setQuery(next);
        onQueryChange(next);
    };

    return (
        <AnimatePresence>
            {open && (
                <>
                    <motion.div
                        key="search-backdrop"
                        className="fixed inset-0 bg-black/40 z-40"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.aside
                        key="search-drawer"
                        className="fixed top-0 right-0 h-full w-[360px] max-w-[90vw] bg-gray-800 shadow-2xl z-50 flex flex-col text-white"
                        role="dialog"
                        aria-label="Search in book"
                        initial={{ x: '100%' }}
                        animate={{ x: 0 }}
                        exit={{ x: '100%' }}
                        transition={{ type: 'tween', duration: 0.2 }}
                    >
                        <div className="h-14 flex items-center justify-between px-4 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Search</h3>
                            <button
                                type="button"
                                aria-label="Close search"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>

                        {disabledReason ? (
                            <p className="px-4 py-6 text-sm text-gray-400">{disabledReason}</p>
                        ) : (
                            <>
                                <div className="px-4 py-3 border-b border-gray-700 shrink-0">
                                    <label className="relative block">
                                        <span className="sr-only">Search query</span>
                                        <Search
                                            size={16}
                                            className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 pointer-events-none"
                                        />
                                        <input
                                            ref={inputRef}
                                            type="search"
                                            value={query}
                                            onChange={(e) => onInput(e.target.value)}
                                            placeholder="Search this book..."
                                            className="w-full bg-gray-900 text-white text-sm rounded-md pl-9 pr-3 py-2 min-h-[44px] focus:outline-none focus:ring-2 focus:ring-blue-400"
                                            aria-label="Search query"
                                            onKeyDown={(e) => e.stopPropagation()}
                                        />
                                    </label>
                                </div>

                                <div className="flex-1 overflow-y-auto">
                                    {busy ? (
                                        <div className="px-4 py-6 flex items-center gap-2 text-sm text-gray-400">
                                            <Loader2 size={16} className="animate-spin" />
                                            Searching...
                                        </div>
                                    ) : query.trim().length === 0 ? (
                                        <p className="px-4 py-6 text-sm text-gray-400">
                                            Type to search the book.
                                        </p>
                                    ) : hits.length === 0 ? (
                                        <p className="px-4 py-6 text-sm text-gray-400">
                                            No matches for &ldquo;{query}&rdquo;.
                                        </p>
                                    ) : (
                                        <ul className="list-none m-0 p-0">
                                            {hits.map((h) => (
                                                <li key={h.key} className="border-b border-gray-700 last:border-b-0">
                                                    <button
                                                        type="button"
                                                        onClick={() => onJump(h)}
                                                        className="w-full text-left px-4 py-3 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-inset"
                                                    >
                                                        {h.label && (
                                                            <div className="text-xs text-gray-400 mb-0.5">{h.label}</div>
                                                        )}
                                                        <div className="text-sm text-gray-100 line-clamp-2">{h.excerpt}</div>
                                                    </button>
                                                </li>
                                            ))}
                                        </ul>
                                    )}
                                </div>
                            </>
                        )}
                    </motion.aside>
                </>
            )}
        </AnimatePresence>
    );
}
