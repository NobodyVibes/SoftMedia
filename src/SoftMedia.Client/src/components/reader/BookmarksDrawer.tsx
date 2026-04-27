import { useEffect, useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { BookmarkPlus, Pencil, Trash2, X } from 'lucide-react';
import type { Bookmark } from '../../services/bookService';

interface BookmarksDrawerProps {
    items: Bookmark[];
    open: boolean;
    /** Adds a bookmark at the current reader position. */
    onAdd: () => void | Promise<void>;
    /** Navigates the reader to the bookmark's stored position/CFI. */
    onJump: (bookmark: Bookmark) => void;
    /** Renames a bookmark. Null clears the label. */
    onRename: (id: string, label: string | null) => void | Promise<void>;
    onDelete: (id: string) => void | Promise<void>;
    onClose: () => void;
}

/**
 * Right-anchored drawer listing this user's bookmarks for the current book.
 * Shares the visual grammar of TocDrawer and ReaderSettingsPanel — same
 * slide, same Escape contract — so the reader's overlay surfaces feel like
 * one idea. Rename is inline (pencil icon → input), Delete is one-click
 * destructive with no confirm (bookmarks are cheap; losing one is low-cost).
 */
export default function BookmarksDrawer({
    items,
    open,
    onAdd,
    onJump,
    onRename,
    onDelete,
    onClose,
}: BookmarksDrawerProps) {
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
                        key="bookmarks-backdrop"
                        className="fixed inset-0 bg-black/40 z-40"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.aside
                        key="bookmarks-drawer"
                        className="fixed top-0 right-0 h-full w-[340px] max-w-[90vw] bg-gray-800 shadow-2xl z-50 flex flex-col text-white"
                        role="dialog"
                        aria-label="Bookmarks"
                        initial={{ x: '100%' }}
                        animate={{ x: 0 }}
                        exit={{ x: '100%' }}
                        transition={{ type: 'tween', duration: 0.2 }}
                    >
                        <div className="h-14 flex items-center justify-between px-4 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Bookmarks</h3>
                            <button
                                type="button"
                                aria-label="Close bookmarks"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>

                        <div className="px-4 py-3 border-b border-gray-700 shrink-0">
                            <button
                                type="button"
                                onClick={onAdd}
                                className="w-full inline-flex items-center justify-center gap-2 min-h-[44px] px-3 py-2 text-sm rounded-md bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <BookmarkPlus size={18} />
                                Add bookmark here
                            </button>
                        </div>

                        <div className="flex-1 overflow-y-auto">
                            {items.length === 0 ? (
                                <p className="px-4 py-6 text-sm text-gray-400">
                                    No bookmarks yet. Press <kbd className="px-1 py-0.5 bg-gray-700 rounded text-xs">b</kbd> while reading to save one.
                                </p>
                            ) : (
                                <ul className="list-none m-0 p-0">
                                    {items.map((b) => (
                                        <BookmarkRow
                                            key={b.id}
                                            bookmark={b}
                                            onJump={() => onJump(b)}
                                            onRename={(label) => onRename(b.id, label)}
                                            onDelete={() => onDelete(b.id)}
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

interface BookmarkRowProps {
    bookmark: Bookmark;
    onJump: () => void;
    onRename: (label: string | null) => void | Promise<void>;
    onDelete: () => void | Promise<void>;
}

function BookmarkRow({ bookmark, onJump, onRename, onDelete }: BookmarkRowProps) {
    const [editing, setEditing] = useState(false);
    const [draft, setDraft] = useState(bookmark.label ?? '');

    const locationLabel = bookmark.position != null
        ? `Page ${bookmark.position}`
        : 'Saved location'; // CFI is long and not user-friendly

    const commit = async () => {
        const trimmed = draft.trim();
        await onRename(trimmed.length === 0 ? null : trimmed);
        setEditing(false);
    };

    return (
        <li className="group border-b border-gray-700 last:border-b-0">
            <div className="flex items-stretch">
                <button
                    type="button"
                    onClick={onJump}
                    className="flex-1 text-left px-4 py-3 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-inset"
                    title="Jump to this bookmark"
                >
                    {editing ? (
                        <form
                            onSubmit={(e) => { e.preventDefault(); commit(); }}
                            onClick={(e) => e.stopPropagation()}
                        >
                            <input
                                autoFocus
                                value={draft}
                                onChange={(e) => setDraft(e.target.value)}
                                onBlur={commit}
                                onKeyDown={(e) => {
                                    if (e.key === 'Escape') {
                                        setEditing(false);
                                        setDraft(bookmark.label ?? '');
                                    }
                                    e.stopPropagation();
                                }}
                                placeholder="Label (optional)"
                                className="w-full bg-gray-900 text-white text-sm rounded px-2 py-1 focus:outline-none focus:ring-1 focus:ring-blue-400"
                                aria-label="Bookmark label"
                            />
                        </form>
                    ) : (
                        <>
                            <div className="text-sm text-gray-100 truncate">
                                {bookmark.label || <span className="text-gray-400 italic">Unlabelled</span>}
                            </div>
                            <div className="text-xs text-gray-400 mt-0.5">{locationLabel}</div>
                        </>
                    )}
                </button>
                <button
                    type="button"
                    aria-label="Rename bookmark"
                    onClick={(e) => { e.stopPropagation(); setEditing(true); setDraft(bookmark.label ?? ''); }}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center text-gray-400 hover:text-gray-100 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Pencil size={16} />
                </button>
                <button
                    type="button"
                    aria-label="Delete bookmark"
                    onClick={(e) => { e.stopPropagation(); onDelete(); }}
                    className="min-w-[44px] min-h-[44px] flex items-center justify-center text-gray-400 hover:text-red-400 hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <Trash2 size={16} />
                </button>
            </div>
        </li>
    );
}
