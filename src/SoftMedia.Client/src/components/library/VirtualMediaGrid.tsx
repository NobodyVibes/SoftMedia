import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import HoverableMediaCardWrapper from '../items/HoverableMediaCardWrapper';
import { type MediaItem, MediaType } from '../../types';

/**
 * Row-virtualized library grid (SR-WI-042).
 *
 * The old LibraryPage grid mounted EVERY fetched card into a CSS grid — a
 * 10k-item library meant 10k live framer-motion cards. This renders only the
 * rows intersecting the viewport (plus overscan) with @tanstack/react-virtual,
 * mirroring the row pattern already used by TVDetailView's episode list.
 *
 * Layout parity: on md+ screens this reproduces the previous
 * `repeat(auto-fill, 192px)` + `gap-8` grid pixel-for-pixel (fixed 192px
 * columns, left-aligned, 32px gaps, 400/300px card heights). Below md it
 * switches to an adaptive `minmax(110px, 1fr)`-equivalent with a tighter gap
 * so phones get 3-ish columns instead of one column and dead margin.
 *
 * The scroll container is MainLayout's <main> (the app shell scrolls there,
 * not on the window), found via closest() so this component needs no layout
 * changes. Cards stay real DOM links/buttons while mounted, so keyboard
 * tabbing and the focus-within play overlay keep working.
 */

// Desktop metrics — MUST match HoverableMediaCardWrapper's defaults (192px
// card, 400px video / 300px audio heights) so desktop is unchanged.
const CARD_WIDTH = 192;
const GAP = 32; // gap-8
// Compact (below Tailwind `md`) metrics: minmax(110px, 1fr)-style fill.
const COMPACT_MIN_CARD_WIDTH = 110;
const COMPACT_GAP = 16;
// Info-strip heights implied by the wrapper's fixed card heights:
// video 400 = 288 (2:3 poster at 192w) + 112; audio 300 = 192 (square) + 108.
const INFO_HEIGHT_VIDEO = 112;
const INFO_HEIGHT_AUDIO = 108;

/**
 * The sequential-reveal cascade only makes sense for the initially-visible
 * window: its cursor advances via per-index image-load events, and virtual
 * rows deeper in the list never mount until scrolled to — waiting on them
 * would stall the cursor (2s stuck-timeout per index). Items past this bound
 * render immediately, matching the virtualized precedent in TVDetailView.
 */
const CASCADE_LIMIT = 30;

interface GridLayout {
    columns: number;
    colWidth: number;
    rowHeight: number;
    gap: number;
}

function computeLayout(containerWidth: number, compact: boolean, audioGrid: boolean): GridLayout {
    const gap = compact ? COMPACT_GAP : GAP;
    let columns: number;
    let colWidth: number;
    if (compact) {
        columns = Math.max(2, Math.floor((containerWidth + gap) / (COMPACT_MIN_CARD_WIDTH + gap)));
        colWidth = Math.floor((containerWidth - (columns - 1) * gap) / columns);
    } else {
        columns = Math.max(1, Math.floor((containerWidth + gap) / (CARD_WIDTH + gap)));
        colWidth = CARD_WIDTH;
    }
    const cardHeight = audioGrid
        ? colWidth + INFO_HEIGHT_AUDIO
        : Math.round(colWidth * 1.5) + INFO_HEIGHT_VIDEO;
    return { columns, colWidth, rowHeight: cardHeight + gap, gap };
}

interface VirtualMediaGridProps {
    items: MediaItem[];
    libraryType?: string;
    hoveredId: string | null;
    setHoveredId: (id: string | null) => void;
    /** Sequential-reveal cascade wiring (useSequentialReveal). */
    isRevealed: (index: number) => boolean;
    onImageLoad: (index: number) => void;
    onImageError: (index: number) => void;
}

export default function VirtualMediaGrid({
    items,
    libraryType,
    hoveredId,
    setHoveredId,
    isRevealed,
    onImageLoad,
    onImageError,
}: VirtualMediaGridProps) {
    const containerRef = useRef<HTMLDivElement>(null);
    const [scrollEl, setScrollEl] = useState<HTMLElement | null>(null);
    const [scrollMargin, setScrollMargin] = useState(0);
    // Music libraries render square audio cards; everything else the 2:3 poster.
    // Row heights must be uniform for the virtualizer, so this is grid-level.
    const audioGrid = libraryType === 'Music';
    const [layout, setLayout] = useState<GridLayout>(() => computeLayout(1200, false, audioGrid));

    useLayoutEffect(() => {
        const container = containerRef.current;
        if (!container) return;
        // The app shell scrolls inside MainLayout's <main>, not the window.
        // Fallback keeps the component functional in isolation (tests).
        const scroller = (container.closest('main') as HTMLElement | null) ?? document.documentElement;
        setScrollEl(scroller);

        // Tailwind `md` breakpoint — guarded for jsdom, which lacks matchMedia.
        const compactQuery = typeof window.matchMedia === 'function'
            ? window.matchMedia('(max-width: 767px)')
            : null;

        const measure = () => {
            const width = container.clientWidth;
            if (width > 0) {
                setLayout(computeLayout(width, compactQuery?.matches ?? false, audioGrid));
            }
            // Distance from the top of the scroller's content to the grid, so
            // virtual row offsets account for the filter bar etc. above us.
            const cRect = container.getBoundingClientRect();
            const sRect = scroller.getBoundingClientRect();
            setScrollMargin(Math.max(0, Math.round(cRect.top - sRect.top + scroller.scrollTop)));
        };
        measure();

        const resizeObserver = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null;
        resizeObserver?.observe(container);
        compactQuery?.addEventListener('change', measure);
        return () => {
            resizeObserver?.disconnect();
            compactQuery?.removeEventListener('change', measure);
        };
    }, [audioGrid]);

    const rowCount = Math.ceil(items.length / layout.columns);
    const virtualizer = useVirtualizer({
        count: rowCount,
        getScrollElement: () => scrollEl,
        estimateSize: () => layout.rowHeight,
        overscan: 3,
        scrollMargin,
    });

    // estimateSize is captured by the virtualizer's measurement cache; a
    // layout change (resize/breakpoint flip) must explicitly invalidate it.
    const { rowHeight } = layout;
    useEffect(() => {
        virtualizer.measure();
    }, [rowHeight, virtualizer]);

    // Per-item height matching HoverableMediaCardWrapper's own isAudio logic,
    // scaled to the current column width (identity at 192px).
    const cardHeightFor = (item: MediaItem) => {
        const itemIsAudio = audioGrid
            || item.type === MediaType.Audio
            || item.type === MediaType.Artist
            || item.type === MediaType.Album;
        return itemIsAudio
            ? layout.colWidth + INFO_HEIGHT_AUDIO
            : Math.round(layout.colWidth * 1.5) + INFO_HEIGHT_VIDEO;
    };

    return (
        <div
            ref={containerRef}
            data-testid="virtual-media-grid"
            style={{ height: `${virtualizer.getTotalSize()}px`, position: 'relative' }}
        >
            {virtualizer.getVirtualItems().map((vRow) => {
                const startIndex = vRow.index * layout.columns;
                const rowItems = items.slice(startIndex, startIndex + layout.columns);
                // translateY creates a stacking context per row, so a hovered
                // card scaling to 1.15 would otherwise paint UNDER the row
                // below it (which comes later in DOM order). Lift the whole
                // row while it hosts the hovered card.
                const rowHasHoveredCard = hoveredId !== null && rowItems.some((it) => it.id === hoveredId);
                return (
                    <div
                        key={vRow.key}
                        style={{
                            position: 'absolute',
                            top: 0,
                            left: 0,
                            width: '100%',
                            transform: `translateY(${vRow.start - scrollMargin}px)`,
                            display: 'grid',
                            gridTemplateColumns: `repeat(${layout.columns}, ${layout.colWidth}px)`,
                            gap: `${layout.gap}px`,
                            zIndex: rowHasHoveredCard ? 30 : undefined,
                        }}
                    >
                        {rowItems.map((item, j) => {
                            const index = startIndex + j;
                            return (
                                <HoverableMediaCardWrapper
                                    key={item.id}
                                    item={item}
                                    hoveredId={hoveredId}
                                    setHoveredId={setHoveredId}
                                    libraryType={libraryType}
                                    width={layout.colWidth}
                                    height={cardHeightFor(item)}
                                    groupReady={index < CASCADE_LIMIT ? isRevealed(index) : true}
                                    onImageLoad={() => onImageLoad(index)}
                                    onImageError={() => onImageError(index)}
                                />
                            );
                        })}
                    </div>
                );
            })}
        </div>
    );
}
