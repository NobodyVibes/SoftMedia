import { useState } from 'react';
import { ArrowRight } from 'lucide-react';
import { type MediaItem } from '../../types';
import HoverableMediaCardWrapper from '../items/HoverableMediaCardWrapper';
import HorizontalScrollList from './HorizontalScrollList';
import useSequentialReveal from '../../hooks/useSequentialReveal';

interface MediaRowProps {
    title: string;
    items: MediaItem[];
    viewAllLink?: string;
    libraryType?: string;
}

export default function MediaRow({ title, items, viewAllLink, libraryType }: MediaRowProps) {
    const [hoveredId, setHoveredId] = useState<string | null>(null);

    // Sequential left-to-right cascade reveal. The browser parallel-loads images,
    // and the cascade's stuck-timeout handles any out-of-order arrivals.
    const reveal = useSequentialReveal(items?.length ?? 0);

    if (!items || items.length === 0) return null;

    return (
        <div className="mb-8 group/row relative z-10 transition-[z-index] duration-0 hover:z-50">
            {/* Section Header */}
            <div className="flex items-center justify-between mb-16 px-6 relative z-10">
                <h2 className="text-2xl font-bold text-white tracking-tight">
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

            <div className="relative overflow-visible">
                <HorizontalScrollList
                    className="-my-24 py-24"
                    gap="gap-8"
                >
                    {/* Scroll Spacer for edge-to-edge alignment */}
                    <div className="flex-shrink-0 w-6 h-1" />

                    {items.map((item, i) => (
                        <div key={item.id} className="flex-shrink-0" style={{ width: '192px' }}>
                            <HoverableMediaCardWrapper
                                item={item}
                                hoveredId={hoveredId}
                                setHoveredId={setHoveredId}
                                libraryType={libraryType}
                                width="100%"
                                groupReady={reveal.isRevealed(i)}
                                onImageLoad={() => reveal.onImageLoad(i)}
                                onImageError={() => reveal.onImageError(i)}
                            />
                        </div>
                    ))}

                    {/* Scroll Spacer for edge-to-edge alignment */}
                    <div className="flex-shrink-0 w-6 h-1" />
                </HorizontalScrollList>
            </div>
        </div>
    );
}

