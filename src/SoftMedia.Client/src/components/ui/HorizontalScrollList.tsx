import { useRef, useState, useEffect, useImperativeHandle, useCallback } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useVirtualizer } from '@tanstack/react-virtual';

export interface HorizontalScrollListHandle {
    scrollToIndex: (index: number) => void;
}

interface HorizontalScrollListProps {
    children?: React.ReactNode;
    className?: string;
    gap?: string;
    showArrows?: boolean;
    showSlider?: boolean;
    /** Opt-in virtualized mode for very long lists. Requires itemCount + renderItem. */
    virtualized?: boolean;
    itemCount?: number;
    /** Per-slot width in px, including the inter-item gap. */
    estimateItemSize?: number;
    /** Explicit scroller height (px) required in virtualized mode, since absolutely-positioned items have no intrinsic height. */
    itemHeightPx?: number;
    renderItem?: (index: number) => React.ReactNode;
    overscan?: number;
    ref?: React.Ref<HorizontalScrollListHandle>;
}

export default function HorizontalScrollList({
    children,
    className = '',
    gap = 'gap-4',
    showArrows = true,
    showSlider = true,
    virtualized = false,
    itemCount = 0,
    estimateItemSize = 304,
    itemHeightPx,
    renderItem,
    overscan = 6,
    ref,
}: HorizontalScrollListProps) {
    const scrollRef = useRef<HTMLDivElement>(null);
    const sliderRef = useRef<HTMLDivElement>(null);
    const thumbRef = useRef<HTMLDivElement>(null);
    const [canScrollLeft, setCanScrollLeft] = useState(false);
    const [canScrollRight, setCanScrollRight] = useState(true);

    // TanStack Virtual returns functions the React Compiler cannot memoize; the
    // compiler skips this component either way, so the warning is informational.
    // eslint-disable-next-line react-hooks/incompatible-library
    const virtualizer = useVirtualizer({
        count: virtualized ? itemCount : 0,
        getScrollElement: () => scrollRef.current,
        estimateSize: () => estimateItemSize,
        horizontal: true,
        overscan,
    });

    useImperativeHandle(ref, () => ({
        scrollToIndex: (index: number) => {
            if (virtualized) {
                virtualizer.scrollToIndex(index, { align: 'start' });
            }
        },
    }), [virtualized, virtualizer]);

    // Update thumb position directly via DOM for smooth performance
    const updateThumbPosition = useCallback(() => {
        if (!scrollRef.current || !thumbRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        const maxScroll = scrollWidth - clientWidth;
        const progress = maxScroll > 0 ? scrollLeft / maxScroll : 0;
        const thumbWidth = Math.max(15, (clientWidth / scrollWidth) * 100);
        const thumbPosition = progress * (100 - thumbWidth);

        thumbRef.current.style.width = `${thumbWidth}%`;
        thumbRef.current.style.marginLeft = `${thumbPosition}%`;
    }, []);

    const updateScrollState = useCallback(() => {
        if (!scrollRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        setCanScrollLeft(scrollLeft > 0);
        setCanScrollRight(scrollLeft < scrollWidth - clientWidth - 10);
        updateThumbPosition();
    }, [updateThumbPosition]);

    // Initialize thumb size on mount and when content changes
    useEffect(() => {
        // Small delay to ensure DOM is measured correctly
        const timer = setTimeout(() => {
            updateThumbPosition();
            updateScrollState();
        }, 50);
        return () => clearTimeout(timer);
    }, [children, virtualized, itemCount, updateThumbPosition, updateScrollState]);

    const scroll = (direction: 'left' | 'right') => {
        if (!scrollRef.current) return;
        const scrollAmount = scrollRef.current.clientWidth * 0.8;
        scrollRef.current.scrollBy({
            left: direction === 'left' ? -scrollAmount : scrollAmount,
            behavior: 'smooth'
        });
    };

    // Handle slider track click to scroll
    const handleSliderClick = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!scrollRef.current || !sliderRef.current || e.target === thumbRef.current) return;
        const rect = sliderRef.current.getBoundingClientRect();
        const clickPosition = (e.clientX - rect.left) / rect.width;
        const maxScroll = scrollRef.current.scrollWidth - scrollRef.current.clientWidth;
        scrollRef.current.scrollTo({
            left: clickPosition * maxScroll,
            behavior: 'smooth'
        });
    };

    // Optimized thumb drag - uses requestAnimationFrame for smooth updates
    const handleThumbDrag = (e: React.MouseEvent) => {
        if (!scrollRef.current || !sliderRef.current) return;
        e.preventDefault();
        e.stopPropagation();

        if (thumbRef.current) {
            thumbRef.current.style.cursor = 'grabbing';
            thumbRef.current.style.transition = 'none';
        }

        const onMove = (moveEvent: MouseEvent) => {
            if (!scrollRef.current || !sliderRef.current) return;
            const rect = sliderRef.current.getBoundingClientRect();
            const position = Math.max(0, Math.min(1, (moveEvent.clientX - rect.left) / rect.width));
            const maxScroll = scrollRef.current.scrollWidth - scrollRef.current.clientWidth;
            scrollRef.current.scrollLeft = position * maxScroll;
        };

        const onUp = () => {
            if (thumbRef.current) {
                thumbRef.current.style.cursor = 'grab';
                thumbRef.current.style.transition = '';
            }
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        };

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    };

    return (
        <div className="relative group/scroll flex flex-col w-full overflow-visible">
            {/* Scrollable Content Container */}
            <div className="relative flex items-center w-full overflow-visible">
                {/* Left Arrow Overlay */}
                {showArrows && (
                    <div className="absolute left-0 top-0 bottom-0 z-[60] flex items-center pointer-events-none">
                        <button
                            onClick={() => scroll('left')}
                            className={`w-12 h-20 bg-black/60 backdrop-blur-md flex items-center justify-center text-white transition-all hover:bg-violet-600 pointer-events-auto opacity-0 group-hover/scroll:opacity-100 rounded-r-xl border-r border-t border-b border-white/10 ${canScrollLeft ? '' : '!opacity-0 !pointer-events-none'}`}
                            aria-label="Scroll left"
                        >
                            <ChevronLeft className="w-8 h-8" />
                        </button>
                    </div>
                )}

                {/* Scrollable Content */}
                <div
                    ref={scrollRef}
                    onScroll={updateScrollState}
                    className={`flex-1 ${virtualized ? '' : `flex ${gap}`} overflow-x-auto scrollbar-none transition-all ${className}`}
                    style={{
                        scrollbarWidth: 'none',
                        msOverflowStyle: 'none',
                        ...(virtualized && itemHeightPx ? { height: `${itemHeightPx}px` } : null),
                    }}
                >
                    {virtualized && renderItem ? (
                        <div
                            style={{
                                width: `${virtualizer.getTotalSize()}px`,
                                height: '100%',
                                position: 'relative',
                            }}
                        >
                            {virtualizer.getVirtualItems().map(vItem => (
                                <div
                                    key={vItem.key}
                                    style={{
                                        position: 'absolute',
                                        top: 0,
                                        left: 0,
                                        height: '100%',
                                        width: `${vItem.size}px`,
                                        transform: `translateX(${vItem.start}px)`,
                                    }}
                                >
                                    {renderItem(vItem.index)}
                                </div>
                            ))}
                        </div>
                    ) : (
                        children
                    )}
                </div>

                {/* Right Arrow Overlay */}
                {showArrows && (
                    <div className="absolute right-0 top-0 bottom-0 z-[60] flex items-center pointer-events-none">
                        <button
                            onClick={() => scroll('right')}
                            className={`w-12 h-20 bg-black/60 backdrop-blur-md flex items-center justify-center text-white transition-all hover:bg-violet-600 pointer-events-auto opacity-0 group-hover/scroll:opacity-100 rounded-l-xl border-l border-t border-b border-white/10 ${canScrollRight ? '' : '!opacity-0 !pointer-events-none'}`}
                            aria-label="Scroll right"
                        >
                            <ChevronRight className="w-8 h-8" />
                        </button>
                    </div>
                )}
            </div>

            {/* Interactive Slider Bar */}
            {showSlider && (
                <div className="px-6 w-full mt-12 relative z-[100]">
                    <div
                        ref={sliderRef}
                        role="button"
                        tabIndex={0}
                        aria-label="Jump to scroll position"
                        onClick={handleSliderClick}
                        onKeyDown={(e) => {
                            if (e.key === 'Enter' || e.key === ' ') {
                                e.preventDefault();
                                // Re-use the click handler's hit-test logic by
                                // synthesizing a centered click — consumers who want
                                // fine-grained keyboard control should use the
                                // ArrowLeft/ArrowRight handlers on the parent scroll list.
                                const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
                                handleSliderClick({
                                    clientX: rect.left + rect.width / 2,
                                    currentTarget: e.currentTarget,
                                } as unknown as React.MouseEvent<HTMLDivElement>);
                            }
                        }}
                        className="h-2 bg-white/10 rounded-full cursor-pointer hover:bg-white/20 focus-visible:bg-white/20 focus-visible:ring-2 focus-visible:ring-primary focus-visible:outline-none transition-all opacity-0 group-hover/scroll:opacity-100"
                    >
                        <div
                            ref={thumbRef}
                            className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full"
                            style={{ width: '20%', cursor: 'grab' }}
                            onMouseDown={handleThumbDrag}
                        />
                    </div>
                </div>
            )}
        </div>
    );
}
