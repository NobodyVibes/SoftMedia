import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { createPortal } from 'react-dom';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import type { CastMember } from '../../types';

interface CastStripItemProps {
    member: CastMember;
}

const CLOSE_DELAY_MS = 120;
const VIEWPORT_PADDING = 8;
const TRIGGER_GAP = 6;
const COLUMN_WIDTH_PX = 150;
const MAX_ROWS_PER_PAGE = 8;
const MAX_COLUMNS_PER_PAGE = 2;

function resolveImageSrc(imageUrl?: string): string | undefined {
    if (!imageUrl) return undefined;
    if (imageUrl.startsWith('/cache/') || imageUrl.startsWith('/api/')) return imageUrl;
    if (imageUrl.startsWith('http')) return `/api/v1/image/proxy?url=${encodeURIComponent(imageUrl)}`;
    return imageUrl;
}

export default function CastStripItem({ member }: CastStripItemProps) {
    const characters = member.characters ?? [];
    const hasMultiple = characters.length > 3;
    const displayRoles = hasMultiple
        ? characters.slice(0, 3).join(' / ') + ' …'
        : characters.join(' / ');

    const imageSrc = resolveImageSrc(member.imageUrl);

    const [isOpen, setIsOpen] = useState(false);
    const [popoverPos, setPopoverPos] = useState<{ top: number; left: number } | null>(null);
    const [layout, setLayout] = useState<{ columnCount: number; itemsPerPage: number; pageCount: number }>({
        columnCount: 1,
        itemsPerPage: Math.max(1, characters.length),
        pageCount: 1,
    });
    const [currentPage, setCurrentPage] = useState(0);
    const triggerRef = useRef<HTMLButtonElement>(null);
    const popoverRef = useRef<HTMLDivElement>(null);
    const closeTimerRef = useRef<number | null>(null);
    const popoverId = useId();

    const clearCloseTimer = () => {
        if (closeTimerRef.current !== null) {
            window.clearTimeout(closeTimerRef.current);
            closeTimerRef.current = null;
        }
    };

    const closeNow = () => {
        clearCloseTimer();
        setIsOpen(false);
        setPopoverPos(null);
    };

    const scheduleClose = () => {
        clearCloseTimer();
        closeTimerRef.current = window.setTimeout(() => {
            const active = document.activeElement;
            if (popoverRef.current?.contains(active)) return;
            if (triggerRef.current?.contains(active)) return;
            setIsOpen(false);
            setPopoverPos(null);
        }, CLOSE_DELAY_MS);
    };

    const openPopover = () => {
        if (!hasMultiple) return;
        clearCloseTimer();

        const n = characters.length;
        const availableWidth = window.innerWidth * 0.9;
        const maxColumnsByWidth = Math.max(1, Math.floor(availableWidth / COLUMN_WIDTH_PX));
        const columnCount = Math.min(
            MAX_COLUMNS_PER_PAGE,
            maxColumnsByWidth,
            Math.max(1, Math.ceil(n / MAX_ROWS_PER_PAGE))
        );
        const itemsPerPage = columnCount * MAX_ROWS_PER_PAGE;
        const pageCount = Math.max(1, Math.ceil(n / itemsPerPage));

        setLayout({ columnCount, itemsPerPage, pageCount });
        setCurrentPage(0);
        setIsOpen(true);
    };

    const goToPrevPage = () => setCurrentPage((p) => Math.max(0, p - 1));
    const goToNextPage = () => setCurrentPage((p) => Math.min(layout.pageCount - 1, p + 1));

    useLayoutEffect(() => {
        if (!isOpen || !triggerRef.current || !popoverRef.current) return;

        const trigger = triggerRef.current.getBoundingClientRect();
        const popover = popoverRef.current.getBoundingClientRect();
        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;

        let left = trigger.left + trigger.width / 2 - popover.width / 2;
        left = Math.max(
            VIEWPORT_PADDING,
            Math.min(left, viewportWidth - popover.width - VIEWPORT_PADDING)
        );

        let top = trigger.bottom + TRIGGER_GAP;
        if (top + popover.height > viewportHeight - VIEWPORT_PADDING) {
            const above = trigger.top - popover.height - TRIGGER_GAP;
            top = above >= VIEWPORT_PADDING ? above : VIEWPORT_PADDING;
        }

        setPopoverPos({ top, left });
    }, [isOpen, currentPage]);

    useEffect(() => () => clearCloseTimer(), []);

    useEffect(() => {
        if (!isOpen) return;

        const onKeyDown = (e: globalThis.KeyboardEvent) => {
            if (e.key === 'Escape') closeNow();
        };
        const onPointerDown = (e: PointerEvent) => {
            const target = e.target as Node | null;
            if (triggerRef.current?.contains(target)) return;
            if (popoverRef.current?.contains(target)) return;
            closeNow();
        };
        const onReposition = () => closeNow();

        document.addEventListener('keydown', onKeyDown);
        document.addEventListener('pointerdown', onPointerDown);
        window.addEventListener('scroll', onReposition, true);
        window.addEventListener('resize', onReposition);
        return () => {
            document.removeEventListener('keydown', onKeyDown);
            document.removeEventListener('pointerdown', onPointerDown);
            window.removeEventListener('scroll', onReposition, true);
            window.removeEventListener('resize', onReposition);
        };
    }, [isOpen]);

    const handleKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
        if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            if (isOpen) closeNow();
            else openPopover();
            return;
        }
        if (isOpen && layout.pageCount > 1) {
            if (e.key === 'ArrowRight') {
                e.preventDefault();
                goToNextPage();
            } else if (e.key === 'ArrowLeft') {
                e.preventDefault();
                goToPrevPage();
            }
        }
    };

    return (
        <div className="flex-shrink-0 w-32 text-center group">
            <div className="w-20 h-20 mx-auto rounded-full bg-gradient-to-br from-blue-600/30 to-violet-600/30 border-2 border-white/10 group-hover:border-violet-500/50 transition-all overflow-hidden flex items-center justify-center">
                {imageSrc ? (
                    <img src={imageSrc} alt={member.name} className="w-full h-full object-cover" />
                ) : (
                    <span className="text-2xl text-gray-400">{member.name.charAt(0) || '?'}</span>
                )}
            </div>

            <p className="mt-2 text-sm text-white font-medium line-clamp-1">{member.name}</p>

            {hasMultiple ? (
                <button
                    ref={triggerRef}
                    type="button"
                    onMouseEnter={openPopover}
                    onMouseLeave={scheduleClose}
                    onFocus={openPopover}
                    onBlur={scheduleClose}
                    onClick={() => (isOpen ? closeNow() : openPopover())}
                    onKeyDown={handleKeyDown}
                    aria-expanded={isOpen}
                    aria-controls={isOpen ? popoverId : undefined}
                    className="mt-0.5 w-full min-h-[44px] px-1 py-2 text-xs text-gray-400 rounded-md line-clamp-2 hover:text-white hover:bg-white/5 focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 transition-colors cursor-pointer"
                >
                    {displayRoles}
                </button>
            ) : (
                <p className="mt-0.5 px-1 py-2 text-xs text-gray-400 line-clamp-2">{displayRoles}</p>
            )}

            {isOpen && createPortal(
                <div
                    ref={popoverRef}
                    id={popoverId}
                    aria-live="polite"
                    onMouseEnter={clearCloseTimer}
                    onMouseLeave={scheduleClose}
                    className="fixed z-[70] bg-black/95 border border-violet-500/40 rounded-lg shadow-2xl backdrop-blur-md p-3 text-left min-w-[160px]"
                    style={{
                        top: popoverPos ? popoverPos.top : 0,
                        left: popoverPos ? popoverPos.left : 0,
                        visibility: popoverPos ? 'visible' : 'hidden',
                        maxWidth: `calc(100vw - ${VIEWPORT_PADDING * 2}px)`,
                    }}
                >
                    <p className="text-[11px] uppercase tracking-wide text-violet-300 font-semibold mb-1.5">
                        Characters
                    </p>
                    {(() => {
                        const start = currentPage * layout.itemsPerPage;
                        const pageItems = characters.slice(start, start + layout.itemsPerPage);
                        const pageRowCount = Math.max(1, Math.ceil(pageItems.length / layout.columnCount));
                        return (
                            <ul
                                className="grid gap-x-4 gap-y-1"
                                style={{
                                    gridTemplateColumns: `repeat(${layout.columnCount}, minmax(130px, 1fr))`,
                                    gridTemplateRows: `repeat(${pageRowCount}, auto)`,
                                    gridAutoFlow: 'column',
                                }}
                            >
                                {pageItems.map((c, i) => (
                                    <li key={start + i} className="text-sm text-white leading-snug">{c}</li>
                                ))}
                            </ul>
                        );
                    })()}
                    {layout.pageCount > 1 && (
                        <div className="mt-3 pt-2 border-t border-white/10 flex items-center justify-between gap-2">
                            <button
                                type="button"
                                onClick={goToPrevPage}
                                disabled={currentPage === 0}
                                aria-label="Previous page"
                                className="p-1.5 rounded-md text-gray-300 disabled:opacity-30 disabled:pointer-events-none hover:bg-white/10 hover:text-white focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 transition-colors"
                            >
                                <ChevronLeft className="w-4 h-4" />
                            </button>
                            <span className="text-[11px] text-gray-400 tabular-nums">
                                {currentPage + 1} / {layout.pageCount}
                            </span>
                            <button
                                type="button"
                                onClick={goToNextPage}
                                disabled={currentPage === layout.pageCount - 1}
                                aria-label="Next page"
                                className="p-1.5 rounded-md text-gray-300 disabled:opacity-30 disabled:pointer-events-none hover:bg-white/10 hover:text-white focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 transition-colors"
                            >
                                <ChevronRight className="w-4 h-4" />
                            </button>
                        </div>
                    )}
                </div>,
                document.body
            )}
        </div>
    );
}
