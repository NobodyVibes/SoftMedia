import { useRef, useState, useEffect } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

interface HorizontalScrollListProps {
    children: React.ReactNode;
    className?: string;
    gap?: string;
    showArrows?: boolean;
    showSlider?: boolean;
}

export default function HorizontalScrollList({
    children,
    className = '',
    gap = 'gap-4',
    showArrows = true,
    showSlider = true
}: HorizontalScrollListProps) {
    const scrollRef = useRef<HTMLDivElement>(null);
    const sliderRef = useRef<HTMLDivElement>(null);
    const thumbRef = useRef<HTMLDivElement>(null);
    const [canScrollLeft, setCanScrollLeft] = useState(false);
    const [canScrollRight, setCanScrollRight] = useState(true);

    // Update thumb position directly via DOM for smooth performance
    const updateThumbPosition = () => {
        if (!scrollRef.current || !thumbRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        const maxScroll = scrollWidth - clientWidth;
        const progress = maxScroll > 0 ? scrollLeft / maxScroll : 0;
        const thumbWidth = Math.max(15, (clientWidth / scrollWidth) * 100);
        const thumbPosition = progress * (100 - thumbWidth);

        thumbRef.current.style.width = `${thumbWidth}%`;
        thumbRef.current.style.marginLeft = `${thumbPosition}%`;
    };

    // Initialize thumb size on mount and when children change
    useEffect(() => {
        // Small delay to ensure DOM is measured correctly
        const timer = setTimeout(() => {
            updateThumbPosition();
            updateScrollState();
        }, 50);
        return () => clearTimeout(timer);
    }, [children]);

    const updateScrollState = () => {
        if (!scrollRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        setCanScrollLeft(scrollLeft > 0);
        setCanScrollRight(scrollLeft < scrollWidth - clientWidth - 10);
        updateThumbPosition();
    };

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
        <div className="relative group/scroll flex flex-col">
            {/* Container with arrow buttons */}
            <div className="flex items-center gap-2 relative z-10 w-full">
                {/* Left Arrow */}
                {showArrows && (
                    <button
                        onClick={() => scroll('left')}
                        className={`flex-shrink-0 w-10 h-10 rounded-full bg-white/10 border border-white/20 flex items-center justify-center text-white transition-all hover:bg-violet-600 hover:border-violet-400 opacity-0 group-hover/scroll:opacity-100 relative z-[100] ${canScrollLeft ? '' : 'pointer-events-none !opacity-0'}`}
                        aria-label="Scroll left"
                    >
                        <ChevronLeft className="w-5 h-5" />
                    </button>
                )}

                {/* Scrollable Content */}
                <div
                    ref={scrollRef}
                    onScroll={updateScrollState}
                    className={`flex-1 flex overflow-x-auto scrollbar-none transition-all ${gap} ${className}`}
                    style={{ scrollbarWidth: 'none', msOverflowStyle: 'none' }}
                >
                    {children}
                </div>

                {/* Right Arrow */}
                {showArrows && (
                    <button
                        onClick={() => scroll('right')}
                        className={`flex-shrink-0 w-10 h-10 rounded-full bg-white/10 border border-white/20 flex items-center justify-center text-white transition-all hover:bg-violet-600 hover:border-violet-400 opacity-0 group-hover/scroll:opacity-100 relative z-[100] ${canScrollRight ? '' : 'pointer-events-none !opacity-0'}`}
                        aria-label="Scroll right"
                    >
                        <ChevronRight className="w-5 h-5" />
                    </button>
                )}
            </div>

            {/* Interactive Slider Bar */}
            {showSlider && (
                <div className="px-6 w-full mt-2 relative z-[100]">
                    <div
                        ref={sliderRef}
                        onClick={handleSliderClick}
                        className="h-2 bg-white/10 rounded-full cursor-pointer hover:bg-white/20 transition-all opacity-0 group-hover/scroll:opacity-100"
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
