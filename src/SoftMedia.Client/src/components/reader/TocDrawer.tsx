import { useCallback, useEffect, useRef } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { X } from 'lucide-react';

/**
 * Shared Table-of-Contents shape across EPUB, PDF, and (future) CBZ. Each
 * format adapts its native outline into this structure before handing it to
 * the drawer. `href` is both the visual identity and the jump target for
 * EPUB (epub.js `rendition.display(href)`); PDF uses `pageNumber` because
 * pdf.js outline destinations resolve to 1-based page numbers.
 */
export interface TocItem {
    label: string;
    href: string;
    pageNumber?: number;
    children?: TocItem[];
}

interface TocDrawerProps {
    items: TocItem[];
    currentHref: string | null;
    open: boolean;
    onJump: (item: TocItem) => void;
    onClose: () => void;
}

/**
 * Right-anchored slide-in drawer listing the book's TOC. Items render as a
 * nested, indented `<ul>`; each leaf is a Universal-Client-compliant button
 * (44×44 hit target, visible focus ring). Closes on Escape / backdrop click
 * / explicit X button, returning focus to the element that opened it — the
 * caller is responsible for focus restoration via onClose.
 */
export default function TocDrawer({ items, currentHref, open, onJump, onClose }: TocDrawerProps) {
    // Close on Escape. Mounted only while open so it never steals Escape from
    // the reader when the drawer is hidden.
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
                        key="toc-backdrop"
                        className="fixed inset-0 bg-black/40 z-40"
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        exit={{ opacity: 0 }}
                        transition={{ duration: 0.15 }}
                        onClick={onClose}
                        aria-hidden
                    />
                    <motion.aside
                        key="toc-drawer"
                        className="fixed top-0 right-0 h-full w-[320px] max-w-[85vw] bg-gray-800 shadow-2xl z-50 flex flex-col text-white"
                        role="dialog"
                        aria-label="Table of contents"
                        initial={{ x: '100%' }}
                        animate={{ x: 0 }}
                        exit={{ x: '100%' }}
                        transition={{ type: 'tween', duration: 0.2 }}
                    >
                        <div className="h-14 flex items-center justify-between px-4 border-b border-gray-700 shrink-0">
                            <h3 className="font-medium">Contents</h3>
                            <button
                                type="button"
                                aria-label="Close contents"
                                onClick={onClose}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                <X size={20} />
                            </button>
                        </div>
                        <nav className="flex-1 overflow-y-auto py-2">
                            {items.length === 0 ? (
                                <p className="px-4 py-6 text-sm text-gray-400">
                                    This book has no table of contents.
                                </p>
                            ) : (
                                <TocList
                                    items={items}
                                    depth={0}
                                    currentHref={currentHref}
                                    onJump={onJump}
                                />
                            )}
                        </nav>
                    </motion.aside>
                </>
            )}
        </AnimatePresence>
    );
}

interface TocListProps {
    items: TocItem[];
    depth: number;
    currentHref: string | null;
    onJump: (item: TocItem) => void;
}

function TocList({ items, depth, currentHref, onJump }: TocListProps) {
    return (
        <ul className="list-none m-0 p-0">
            {items.map((item, i) => (
                <TocRow
                    key={`${depth}-${i}-${item.href}`}
                    item={item}
                    depth={depth}
                    currentHref={currentHref}
                    onJump={onJump}
                />
            ))}
        </ul>
    );
}

interface TocRowProps {
    item: TocItem;
    depth: number;
    currentHref: string | null;
    onJump: (item: TocItem) => void;
}

function TocRow({ item, depth, currentHref, onJump }: TocRowProps) {
    const isCurrent = currentHref !== null && hrefMatches(item.href, currentHref);
    const buttonRef = useRef<HTMLButtonElement | null>(null);

    const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLButtonElement>) => {
        // Arrow keys move focus between visible TOC buttons, matching typical
        // menu patterns. Page-turn arrows are intercepted here so they don't
        // leak back to the reader when the drawer is open.
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            e.stopPropagation();
            const root = buttonRef.current?.closest('[role="dialog"]');
            if (!root) return;
            const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('button[data-toc-item="true"]'));
            const idx = buttons.indexOf(buttonRef.current!);
            if (idx === -1) return;
            const next = e.key === 'ArrowDown'
                ? buttons[Math.min(idx + 1, buttons.length - 1)]
                : buttons[Math.max(idx - 1, 0)];
            next?.focus();
        }
    }, []);

    return (
        <li>
            <button
                ref={buttonRef}
                type="button"
                data-toc-item="true"
                onClick={() => onJump(item)}
                onKeyDown={handleKeyDown}
                className={`w-full text-left min-h-[44px] px-4 py-2 text-sm truncate rounded-sm hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-inset ${
                    isCurrent
                        ? 'bg-gradient-to-r from-blue-500/20 to-purple-500/20 text-white font-medium'
                        : 'text-gray-200'
                }`}
                style={{ paddingLeft: `${16 + depth * 14}px` }}
                title={item.label}
            >
                {item.label}
            </button>
            {item.children && item.children.length > 0 && (
                <TocList
                    items={item.children}
                    depth={depth + 1}
                    currentHref={currentHref}
                    onJump={onJump}
                />
            )}
        </li>
    );
}

/**
 * EPUB TOCs reference spine items by href with an optional fragment anchor.
 * `tocChanged` emits the href exactly as authored, but `rendition.relocated`
 * reports the current resource without the fragment. Compare on the base path
 * so chapter highlighting works even when the user is mid-chapter.
 */
function hrefMatches(candidate: string, current: string): boolean {
    if (candidate === current) return true;
    const baseCandidate = candidate.split('#')[0];
    const baseCurrent = current.split('#')[0];
    return baseCandidate === baseCurrent || baseCurrent.endsWith(baseCandidate);
}
