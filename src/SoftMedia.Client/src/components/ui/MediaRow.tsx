import { useRef } from 'react';
import { ChevronLeft, ChevronRight, ArrowRight } from 'lucide-react';
import { type MediaItem } from '../../types';
import MediaCard from '../items/MediaCard';

interface MediaRowProps {
    title: string;
    items: MediaItem[];
    viewAllLink?: string;
}

export default function MediaRow({ title, items, viewAllLink }: MediaRowProps) {
    const rowRef = useRef<HTMLDivElement>(null);

    const scroll = (direction: 'left' | 'right') => {
        if (rowRef.current) {
            const { current } = rowRef;
            const scrollAmount = direction === 'left'
                ? -current.offsetWidth
                : current.offsetWidth;

            current.scrollBy({ left: scrollAmount, behavior: 'smooth' });
        }
    };

    if (!items || items.length === 0) return null;

    return (
        <div className="mb-10 group/row">
            {/* Section Header */}
            <div className="flex items-center justify-between mb-4 px-6">
                <h2 className="text-2xl font-bold text-white">
                    {title}
                </h2>
                {viewAllLink && (
                    <a
                        href={viewAllLink}
                        className="flex items-center gap-1 text-sm text-gray-400 hover:text-white transition-colors group"
                    >
                        <span>VIEW ALL</span>
                        <ArrowRight size={16} className="group-hover:translate-x-1 transition-transform" />
                    </a>
                )}
            </div>

            <div className="relative px-6">
                {/* Left Arrow */}
                <button
                    onClick={() => scroll('left')}
                    className="absolute left-0 top-0 bottom-4 z-10 bg-gradient-to-r from-background via-background/95 to-transparent hover:from-background hover:via-background/98 p-2 opacity-0 group-hover/row:opacity-100 transition-all flex items-center justify-start w-16"
                    aria-label="Scroll left"
                >
                    <div className="bg-black/70 hover:bg-black/90 rounded-full p-2 transition-colors">
                        <ChevronLeft className="text-white" size={24} />
                    </div>
                </button>

                {/* Scroll Container */}
                <div
                    ref={rowRef}
                    className="flex gap-3 overflow-x-auto pb-4 scroll-smooth"
                    style={{ scrollbarWidth: 'none', msOverflowStyle: 'none' }}
                >
                    {items.map((item) => (
                        <div key={item.id} className="flex-none w-[180px]">
                            <MediaCard item={item} />
                        </div>
                    ))}
                </div>

                {/* Right Arrow */}
                <button
                    onClick={() => scroll('right')}
                    className="absolute right-0 top-0 bottom-4 z-10 bg-gradient-to-l from-background via-background/95 to-transparent hover:from-background hover:via-background/98 p-2 opacity-0 group-hover/row:opacity-100 transition-all flex items-center justify-end w-16"
                    aria-label="Scroll right"
                >
                    <div className="bg-black/70 hover:bg-black/90 rounded-full p-2 transition-colors">
                        <ChevronRight className="text-white" size={24} />
                    </div>
                </button>
            </div>
        </div>
    );
}

