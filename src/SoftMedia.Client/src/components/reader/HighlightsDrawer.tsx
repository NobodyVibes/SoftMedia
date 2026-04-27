import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { Download, Trash2, X } from 'lucide-react';
import type { Highlight, HighlightColour } from '../../services/bookService';

interface HighlightsDrawerProps {
    items: Highlight[];
    bookTitle: string;
    open: boolean;
    onJump: (h: Highlight) => void;
    onDelete: (id: string) => void | Promise<void>;
    onChangeColour: (id: string, colour: HighlightColour) => void | Promise<void>;
    onChangeNote: (id: string, note: string | null) => void | Promise<void>;
    onClose: () => void;
    /**
     * When non-null on open, the named highlight's row auto-starts in note-
     * edit mode with focus — drives the "Highlight + note" flow from the
     * floating selection toolbar. The caller should reset this to null after
     * the drawer closes so re-opening later doesn't re-trigger the edit.
     */
    autoEditNoteId?: string | null;
}

const COLOUR_PALETTE: { value: HighlightColour; label: string; swatch: string }[] = [
    { value: 'yellow', label: 'Yellow', swatch: '#fde68a' },
    { value: 'green', label: 'Green', swatch: '#a7f3d0' },
    { value: 'blue', label: 'Blue', swatch: '#bfdbfe' },
    { value: 'pink', label: 'Pink', swatch: '#fbcfe8' },
    { value: 'orange', label: 'Orange', swatch: '#fed7aa' },
];

/**
 * Looks up the swatch colour for a stored highlight value. Unknown values
 * (e.g., a user migrating from a future palette) fall back to yellow so the
 * list still renders.
 */
export function swatchFor(colour: string): string {
    return COLOUR_PALETTE.find((p) => p.value === colour)?.swatch ?? '#fde68a';
}

/**
 * Right-anchored drawer listing this user's highlights for the current book.
 * Inline colour picker, inline note editor, and a "Copy all as Markdown"
 * export button at the top — export is local-first, runs in-browser, no
 * network calls beyond the initial listHighlights.
 */
export default function HighlightsDrawer({
    items,
    bookTitle,
    open,
    onJump,
    onDelete,
    onChangeColour,
    onChangeNote,
    onClose,
    autoEditNoteId,
}: HighlightsDrawerProps) {
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

    const exportMarkdown = () => {
        // Built on demand to avoid keeping a giant string in memory for
        // heavily-highlighted books. Blob + URL.createObjectURL hands the
        // bytes straight to the save-as dialog — SoftMedia is local-first so
        // there is no server round-trip.
        const lines: string[] = [];
        lines.push(`# Highlights — ${bookTitle}`);
        lines.push('');
        for (const h of items) {
            lines.push(`> ${h.quotedText.replace(/\n/g, '\n> ')}`);
            if (h.note) {
                lines.push('');
                lines.push(h.note);
            }
            lines.push('');
            lines.push('---');
            lines.push('');
        }
        const blob = new Blob([lines.join('\n')], { type: 'text/markdown' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${bookTitle.replace(/[^\w\s-]/g, '').trim() || 'highlights'}.md`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    };

    return (
        <AnimatePresence>
            {open && (
                <>
                    <motion.div
                        key="highlights-backdrop"
                        className="fixed inset-0 bg-black/40 z-40"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.aside
                        key="highlights-drawer"
                        className="fixed top-0 right-0 h-full w-[380px] max-w-[95vw] bg-gray-800 shadow-2xl z-50 flex flex-col text-white"
                        role="dialog"
                        aria-label="Highlights"
                        initial={{ x: '100%' }}
                        animate={{ x: 0 }}
                        exit={{ x: '100%' }}
                        transition={{ type: 'tween', duration: 0.2 }}
                    >
                        <div className="h-14 flex items-center justify-between px-4 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Highlights</h3>
                            <button
                                type="button"
                                aria-label="Close highlights"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>

                        <div className="px-4 py-3 border-b border-gray-700 shrink-0">
                            <button
                                type="button"
                                onClick={exportMarkdown}
                                disabled={items.length === 0}
                                className="w-full inline-flex items-center justify-center gap-2 min-h-[44px] px-3 py-2 text-sm rounded-md bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow disabled:opacity-30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <Download size={18} />
                                Copy all as Markdown
                            </button>
                        </div>

                        <div className="flex-1 overflow-y-auto">
                            {items.length === 0 ? (
                                <p className="px-4 py-6 text-sm text-gray-400">
                                    No highlights yet. Select text while reading to save one.
                                </p>
                            ) : (
                                <ul className="list-none m-0 p-0">
                                    {items.map((h) => (
                                        <HighlightRow
                                            key={h.id}
                                            highlight={h}
                                            onJump={() => onJump(h)}
                                            onDelete={() => onDelete(h.id)}
                                            onChangeColour={(c) => onChangeColour(h.id, c)}
                                            onChangeNote={(n) => onChangeNote(h.id, n)}
                                            autoEditNote={autoEditNoteId === h.id}
                                        />
                                    ))}
                                </ul>
                            )}
                        </div>
                    </motion.aside>
                </>
            )}
        </AnimatePresence>
    );
}

interface HighlightRowProps {
    highlight: Highlight;
    onJump: () => void;
    onDelete: () => void | Promise<void>;
    onChangeColour: (colour: HighlightColour) => void | Promise<void>;
    onChangeNote: (note: string | null) => void | Promise<void>;
    /** Open the note editor on mount and scroll this row into view. Set by
     *  the drawer when arriving from the "Highlight + note" floating button. */
    autoEditNote?: boolean;
}

function HighlightRow({ highlight, onJump, onDelete, onChangeColour, onChangeNote, autoEditNote }: HighlightRowProps) {
    const [editingNote, setEditingNote] = useState(!!autoEditNote);
    const [noteDraft, setNoteDraft] = useState(highlight.note ?? '');
    const rootRef = useRef<HTMLLIElement | null>(null);

    // Auto-edit signalled from parent: scroll into view and enter edit mode
    // once. The effect runs when autoEditNote flips to true — on subsequent
    // changes the user's own toggles drive `editingNote`.
    useEffect(() => {
        if (!autoEditNote) return;
        setEditingNote(true);
        rootRef.current?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }, [autoEditNote]);

    const commitNote = async () => {
        const trimmed = noteDraft.trim();
        await onChangeNote(trimmed.length === 0 ? null : trimmed);
        setEditingNote(false);
    };

    return (
        <li ref={rootRef} className="border-b border-gray-700 last:border-b-0 px-4 py-3">
            <div
                role="button"
                tabIndex={0}
                onClick={onJump}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onJump(); } }}
                className="block cursor-pointer rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                aria-label="Jump to highlight"
            >
                <blockquote
                    className="text-sm text-gray-100 border-l-4 pl-3 py-1 line-clamp-5"
                    style={{ borderLeftColor: swatchFor(highlight.colour) }}
                >
                    {highlight.quotedText}
                </blockquote>
            </div>

            {editingNote ? (
                <form
                    className="mt-2"
                    onSubmit={(e) => { e.preventDefault(); commitNote(); }}
                    onClick={(e) => e.stopPropagation()}
                >
                    <textarea
                        autoFocus
                        value={noteDraft}
                        onChange={(e) => setNoteDraft(e.target.value)}
                        onBlur={commitNote}
                        onKeyDown={(e) => {
                            if (e.key === 'Escape') {
                                setEditingNote(false);
                                setNoteDraft(highlight.note ?? '');
                            }
                            e.stopPropagation();
                        }}
                        placeholder="Note (optional)"
                        className="w-full bg-gray-900 text-white text-sm rounded px-2 py-1 focus:outline-none focus:ring-1 focus:ring-blue-400"
                        rows={3}
                        aria-label="Highlight note"
                    />
                </form>
            ) : highlight.note ? (
                <button
                    type="button"
                    onClick={() => { setEditingNote(true); setNoteDraft(highlight.note ?? ''); }}
                    className="mt-2 w-full text-left text-xs text-gray-300 italic block rounded px-2 py-1 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    {highlight.note}
                </button>
            ) : null}

            <div className="flex items-center gap-1 mt-2">
                {COLOUR_PALETTE.map((c) => (
                    <button
                        key={c.value}
                        type="button"
                        aria-label={`Change to ${c.label}`}
                        aria-pressed={highlight.colour === c.value}
                        onClick={() => onChangeColour(c.value)}
                        className={`w-6 h-6 rounded-full border-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                            highlight.colour === c.value ? 'border-white' : 'border-transparent'
                        }`}
                        style={{ backgroundColor: c.swatch }}
                    />
                ))}
                <div className="flex-1" />
                {!highlight.note && !editingNote && (
                    <button
                        type="button"
                        onClick={() => { setEditingNote(true); setNoteDraft(''); }}
                        className="text-xs text-gray-300 hover:text-white hover:bg-gray-700 px-2 py-1 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        Add note
                    </button>
                )}
                <button
                    type="button"
                    aria-label="Delete highlight"
                    onClick={onDelete}
                    className="min-w-[32px] min-h-[32px] flex items-center justify-center text-gray-400 hover:text-red-400 hover:bg-gray-700 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Trash2 size={14} />
                </button>
            </div>
        </li>
    );
}

export { COLOUR_PALETTE };
